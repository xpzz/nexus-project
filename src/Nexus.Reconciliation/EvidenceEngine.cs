using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Core.Configuration;
using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

/// <summary>Asset types the inventory distinguishes (ADR-0007). BYOD is an ownership, not a type: an iPhone can be a phone and BYOD.</summary>
public static class AssetTypes
{
    public const string Desktop = "desktop", Notebook = "notebook", Server = "server", Phone = "phone", Tablet = "tablet", Mac = "mac",
        Shared = "shared", Kiosk = "kiosk", Iot = "iot", Unknown = "unknown";

    public static readonly string[] All = [Desktop, Notebook, Server, Phone, Tablet, Mac, Shared, Kiosk, Iot, Unknown];

    public static string Title(string type) => type switch
    {
        Desktop => "Desktop",
        Notebook => "Notebook",
        Server => "Servidor",
        Phone => "Celular",
        Tablet => "Tablet",
        Mac => "Mac",
        Shared => "Compartilhado",
        Kiosk => "Quiosque",
        Iot => "IoT",
        _ => "Tipo não identificado",
    };
}

/// <summary>Operational state of an asset, decided from independent evidence (ADR-0007).</summary>
public static class OperationalStates
{
    public const string Confirmed = "ConfirmedActive", Probable = "ProbableActive", Inactive = "Inactive", NoRecent = "NoRecentEvidence",
        Conflicting = "Conflicting", Unknown = "Unknown", Decommissioned = "Decommissioned";

    public static readonly string[] All = [Confirmed, Probable, NoRecent, Inactive, Conflicting, Unknown, Decommissioned];

    public static string Title(string state) => state switch
    {
        Confirmed => "Ativo confirmado",
        Probable => "Ativo provável",
        NoRecent => "Sem evidência recente",
        Inactive => "Inativo ou obsoleto",
        Conflicting => "Conflitante",
        Unknown => "Desconhecido",
        Decommissioned => "Excluído ou descomissionado",
        _ => state,
    };

    public static string Explain(string state) => state switch
    {
        Confirmed => "Duas ou mais ferramentas independentes reportaram dentro da janela de confirmação.",
        Probable => "Uma ferramenta que roda no equipamento reportou dentro da janela de probabilidade, ou duas fontes de identidade concordam.",
        NoRecent => "Há algum relato dentro da janela de observação, mas nada recente o bastante para afirmar que o equipamento está em uso.",
        Inactive => "Nenhuma fonte reportou dentro da janela de observação. Candidato a revisão ou inativação.",
        Conflicting => "A identidade do equipamento é ambígua (por exemplo serial duplicado). Resolva o conflito antes de confiar na contagem.",
        Unknown => "Nenhuma fonte trouxe data de atividade para este equipamento.",
        Decommissioned => "Marcado como obsoleto no SCCM ou desabilitado no AD, e sem telemetria recente.",
        _ => "",
    };

    public static bool IsActive(string state) => state is Confirmed or Probable;

    /// <summary>A symbol next to the name so the state never depends on color alone.</summary>
    public static string Icon(string state) => state switch
    {
        Confirmed => "✔",
        Probable => "◐",
        NoRecent => "○",
        Inactive => "⊘",
        Conflicting => "⚠",
        Decommissioned => "✕",
        _ => "?",
    };

    /// <summary>Pill class: ok, a (accent), w (attention), b (problem), m (neutral).</summary>
    public static string Css(string state) => state switch
    {
        Confirmed => "ok",
        Probable => "a",
        NoRecent => "w",
        Conflicting => "b",
        _ => "m",
    };
}

/// <summary>The most recent report among the tools that know the asset, for the "last evidence" column.</summary>
public static class LastEvidence
{
    public static (string Label, DateTimeOffset At)? Of(Asset a)
    {
        // Telemetry first: a device that reports itself says more than the identity side that merely knows it.
        var latest = ActivityModel.Signals(a).Where(s => s.At is not null).OrderByDescending(s => s.Strong).ThenByDescending(s => s.At).FirstOrDefault();
        return latest is null ? null : (latest.Label, latest.At!.Value);
    }
}

public enum SignalTier { Telemetry, Identity }

/// <summary>One tool's last report and how much it counted: reliability times freshness (1 inside the confirmed window, 0.6 inside the probable one, 0.25 inside the observation one).</summary>
public sealed record EvidenceSignal(string Key, string Label, DateTimeOffset? At, SignalTier Tier, double Reliability, double Freshness)
{
    public double Probability => Math.Round(Reliability * Freshness, 3);

    public bool Counts => At is not null && Freshness > 0;
}

/// <summary>Thresholds and trust levels the engine runs with. Built from <see cref="EvidenceSettings"/>; no date is hard-coded in the rules.</summary>
public sealed record EvidencePolicy(EvidenceThresholds Default, IReadOnlyDictionary<string, EvidenceThresholds> ByType, IReadOnlyDictionary<string, double> Reliability, int MinConfirmedSources,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TypeNameTokens)
{
    public static EvidencePolicy From(EvidenceSettings s) => new(s.Default.Normalized(), s.ByType.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value.Normalized()), s.Reliability,
        Math.Clamp(s.MinConfirmedSources, 1, 5), s.TypeNameTokens.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => (IReadOnlyList<string>)kv.Value));

    /// <summary>Default policy whose probable window is the legacy single activity window.</summary>
    public static EvidencePolicy FromWindow(TimeSpan window)
    {
        var s = new EvidenceSettings();
        s.Default.ProbableDays = (int)Math.Max(1, window.TotalDays);
        s.ByType.Clear();
        return From(s);
    }

    public EvidenceThresholds For(string type) => ByType.GetValueOrDefault(type) ?? Default;

    public double ReliabilityOf(string key) => Reliability.TryGetValue(key, out var r) ? Math.Clamp(r, 0, 1) : 0.3;
}

public sealed record EvidenceResult(string AssetType, string State, string ActivityLevel, int Score, IReadOnlyList<EvidenceSignal> Signals, string Explanation, EvidenceThresholds Thresholds, bool DecommissionCandidate)
{
    public bool IsActive => OperationalStates.IsActive(ActivityLevel);
}

/// <summary>
/// Decides whether an asset is in use from recent, independent signals (ADR-0007). Telemetry (SCCM client, Intune check-in, XDR and Netskope agents,
/// protected apps) outweighs identity (Entra sign-in, AD logon). The score is the chance that at least one signal is right: 1 - product(1 - reliability x freshness).
/// Identity conflicts and decommission marks override the activity level for the state, but never erase it: the level is kept for the explanation.
/// </summary>
public static class EvidenceEngine
{
    private static readonly Regex Notebooks = new(@"latitude|thinkpad|elitebook|probook|xps|macbook|surface (laptop|book|go)|laptop|notebook|zenbook|vivobook|ideapad|inspiron 1[3-7]|spectre|envy", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Desktops = new(@"optiplex|prodesk|elitedesk|thinkcentre|precision (t|3[0-9]{3} tower)|tower|desktop|\bsff\b|mini pc|imac|mac mini|mac studio|veriton|vostro 3[0-9]{3} s", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tablets = new(@"ipad|\btab\b|tablet|galaxy tab|surface (pro|go)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string TypeOf(Asset a, EvidencePolicy policy)
    {
        var tokens = NameTokens(a.Name);
        bool Named(string type) => policy.TypeNameTokens.TryGetValue(type, out var list) && list.Any(t => tokens.Contains(t.ToUpperInvariant()));
        var hardware = $"{a.Manufacturer} {a.Model}";

        if (a.Platform == "WindowsServer" || (a.OperatingSystem ?? "").Contains("Windows Server", StringComparison.OrdinalIgnoreCase))
        {
            return AssetTypes.Server;
        }

        // The hardware chassis is the most reliable answer for computers; names and models only guess.
        if (a.Platform is not "iOS" and not "Android")
        {
            switch (a.Chassis)
            {
                case "laptop": return AssetTypes.Notebook;
                case "desktop": return Named("kiosk") ? AssetTypes.Kiosk : Named("shared") ? AssetTypes.Shared : AssetTypes.Desktop;
                case "server": return AssetTypes.Server;
                case "tablet": return AssetTypes.Tablet;
            }
        }

        if (a.Platform is "iOS" or "Android")
        {
            return Tablets.IsMatch(hardware) ? AssetTypes.Tablet : AssetTypes.Phone;
        }

        if (Named("kiosk")) { return AssetTypes.Kiosk; }
        if (Named("shared")) { return AssetTypes.Shared; }
        if (Named("iot")) { return AssetTypes.Iot; }

        if (a.Platform == "macOS" || (a.OperatingSystem ?? "").Contains("macOS", StringComparison.OrdinalIgnoreCase))
        {
            return AssetTypes.Mac;
        }

        if (a.Platform == "WindowsClient" || (a.OperatingSystem ?? "").Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            if (Notebooks.IsMatch(hardware) || Named("notebook")) { return AssetTypes.Notebook; }
            if (Desktops.IsMatch(hardware) || Named("desktop")) { return AssetTypes.Desktop; }
        }

        return AssetTypes.Unknown;
    }

    /// <summary>Upper-case name pieces split on punctuation and digit boundaries: AZ-NB-00135 gives AZ, NB.</summary>
    private static HashSet<string> NameTokens(string? name) =>
        Regex.Split((name ?? "").ToUpperInvariant(), @"[^A-Z]+").Where(t => t.Length > 0).ToHashSet();

    public static IReadOnlyList<EvidenceSignal> Signals(Asset a, DateTimeOffset now, EvidenceThresholds th, EvidencePolicy policy)
    {
        double Fresh(DateTimeOffset? at)
        {
            if (at is null) { return 0; }
            var age = (now - at.Value).TotalDays;
            return age <= th.ConfirmedDays ? 1.0 : age <= th.ProbableDays ? 0.6 : age <= th.NoRecentDays ? 0.25 : 0;
        }

        EvidenceSignal Make(string key, string label, DateTimeOffset? at, SignalTier tier) => new(key, label, at, tier, policy.ReliabilityOf(key), Fresh(at));

        return
        [
            Make("sccm", "SCCM (contato do cliente)", a.SccmLastSeenAt, SignalTier.Telemetry),
            Make("intune", "Intune (sincronização)", a.IntuneLastSyncAt, SignalTier.Telemetry),
            Make("xdr", "Cortex XDR", a.XdrLastSeenAt, SignalTier.Telemetry),
            Make("netskope", "Netskope", a.NetskopeLastSeenAt, SignalTier.Telemetry),
            Make("mam", "Proteção de apps (MAM)", a.MamLastSyncAt, SignalTier.Telemetry),
            Make("m365", "Microsoft 365 (acesso)", a.LastM365AccessAt, SignalTier.Telemetry),
            Make("entra", "Entra ID (login)", a.EntraLastSignInAt, SignalTier.Identity),
            Make("ad", "Active Directory (logon)", a.AdLastLogonAt, SignalTier.Identity),
        ];
    }

    public static EvidenceResult Evaluate(Asset a, DateTimeOffset now, EvidencePolicy policy)
    {
        var type = TypeOf(a, policy);
        var th = policy.For(type);
        var signals = Signals(a, now, th, policy);
        var dated = signals.Where(s => s.At is not null).ToList();

        var inConfirmed = dated.Where(s => (now - s.At!.Value).TotalDays <= th.ConfirmedDays).ToList();
        var inProbable = dated.Where(s => (now - s.At!.Value).TotalDays <= th.ProbableDays).ToList();
        var inObservation = dated.Where(s => (now - s.At!.Value).TotalDays <= th.NoRecentDays).ToList();

        string level;
        if (dated.Count == 0)
        {
            level = OperationalStates.Unknown;
        }
        else if (inConfirmed.Count >= policy.MinConfirmedSources && inConfirmed.Any(s => s.Tier == SignalTier.Telemetry))
        {
            level = OperationalStates.Confirmed;
        }
        else if (inProbable.Any(s => s.Tier == SignalTier.Telemetry) || inProbable.Count >= 2)
        {
            level = OperationalStates.Probable;
        }
        else if (inObservation.Count > 0)
        {
            level = OperationalStates.NoRecent;
        }
        else
        {
            level = OperationalStates.Inactive;
        }

        var noTelemetryRecently = !inProbable.Any(s => s.Tier == SignalTier.Telemetry);
        var markedGone = a.SccmHealth == "Obsolete" || (a.InAd && !a.AdEnabled);
        var state = level;
        if (markedGone && noTelemetryRecently)
        {
            state = OperationalStates.Decommissioned;
        }
        else if (a.NeedsReview)
        {
            state = OperationalStates.Conflicting;
        }

        var miss = signals.Where(s => s.Counts).Aggregate(1.0, (acc, s) => acc * (1 - s.Probability));
        var score = (int)Math.Round(100 * (1 - miss));
        var oldest = dated.Count == 0 ? (double?)null : (now - dated.Max(s => s.At!.Value)).TotalDays;
        var candidate = level is OperationalStates.Inactive && oldest is { } days && days > th.DecommissionDays;
        return new EvidenceResult(type, state, level, score, signals, Explain(state, level, score, signals, now, th, candidate, markedGone), th, candidate);
    }

    public static string Explain(string state, string level, int score, IReadOnlyList<EvidenceSignal> signals, DateTimeOffset now, EvidenceThresholds th, bool candidate, bool markedGone)
    {
        var counted = signals.Where(s => s.Counts).OrderByDescending(s => s.At).ToList();
        var stale = signals.Where(s => s.At is not null && !s.Counts).OrderByDescending(s => s.At).ToList();
        string Part(EvidenceSignal s) => $"{s.Label} {Age(now - s.At!.Value)}";

        var head = $"{OperationalStates.Title(state)}, score {score}";
        string body;
        if (level == OperationalStates.Unknown)
        {
            body = "nenhuma fonte trouxe data de atividade";
        }
        else if (counted.Count > 0)
        {
            body = Join(counted.Select(Part));
            if (stale.Count > 0 && level is OperationalStates.NoRecent or OperationalStates.Probable)
            {
                body += "; fora da janela: " + Join(stale.Take(2).Select(Part));
            }
        }
        else
        {
            body = "último relato " + Join(stale.Take(3).Select(Part)) + $", todos com mais de {th.NoRecentDays} dias";
        }

        var extra = state switch
        {
            OperationalStates.Conflicting => $" Atividade pelas evidências: {OperationalStates.Title(level).ToLowerInvariant()}. A identidade está em conflito e precisa de revisão.",
            OperationalStates.Decommissioned => markedGone ? " Marcado como obsoleto no SCCM ou desabilitado no AD." : "",
            _ => "",
        };
        if (candidate)
        {
            extra += $" Sem relato há mais de {th.DecommissionDays} dias: candidato à inativação.";
        }

        return $"{head}: {body}.{extra}";
    }

    public static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count switch { 0 => "", 1 => list[0], _ => string.Join(", ", list.Take(list.Count - 1)) + " e " + list[^1] };
    }

    public static string Age(TimeSpan span) => span.TotalMinutes < 0 ? "agora"
        : span.TotalHours < 1 ? $"há {Math.Max(1, (int)span.TotalMinutes)} min"
        : span.TotalHours < 24 ? $"há {(int)span.TotalHours} {((int)span.TotalHours == 1 ? "hora" : "horas")}"
        : span.TotalDays < 120 ? $"há {(int)span.TotalDays} {((int)span.TotalDays == 1 ? "dia" : "dias")}"
        : $"há {(int)(span.TotalDays / 30)} meses";

    /// <summary>Writes the engine's verdict into the asset and keeps the legacy pool fields (ActivityClass, IsActive) consistent with the type's own window.</summary>
    public static EvidenceResult Apply(Asset a, DateTimeOffset now, EvidencePolicy policy)
    {
        var result = Evaluate(a, now, policy);
        ActivityModel.Apply(a, now, TimeSpan.FromDays(result.Thresholds.ProbableDays));
        a.AssetType = result.AssetType;
        a.OperationalState = result.State;
        a.ActivityLevel = result.ActivityLevel;
        a.ActivityScore = result.Score;
        a.ActivityExplanation = result.Explanation;
        a.DecommissionCandidate = result.DecommissionCandidate;
        a.EvidenceJson = JsonSerializer.Serialize(result.Signals.Where(s => s.At is not null).Select(s => new { k = s.Key, l = s.Label, at = s.At, t = s.Tier == SignalTier.Telemetry ? "t" : "i", p = s.Probability }));
        return result;
    }
}
