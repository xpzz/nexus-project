using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

public static class ActivityClasses
{
    public const string Confirmed = "Confirmed", Single = "Single", Unconfirmed = "Unconfirmed", Inactive = "Inactive";

    public static string Title(string c) => c switch
    {
        Confirmed => "Confirmada",
        Single => "Uma fonte",
        Unconfirmed => "Não confirmada",
        _ => "Inativa",
    };

    public static string Explain(string c) => c switch
    {
        Confirmed => "Duas ou mais ferramentas reportaram dentro da janela.",
        Single => "Só uma ferramenta que roda no equipamento reportou dentro da janela.",
        Unconfirmed => "Só o lado de identidade (AD ou Entra ID) viu o equipamento, e isso sozinho não prova uso.",
        _ => "Nenhuma ferramenta reportou dentro da janela.",
    };
}

/// <summary>One tool's last report of a device and how much it proves (ADR-0006).</summary>
public sealed record ActivitySignal(string Key, string Label, DateTimeOffset? At, bool Strong);

/// <summary>
/// Correlates the last-report dates of every tool into one verdict. A tool that runs on the device (SCCM client, Intune check-in, XDR and
/// Netskope agents, protected apps) is strong evidence of use. The identity side (AD <c>lastLogonTimestamp</c>, replicated with up to two
/// weeks of lag, and the approximate Entra sign-in) is weak: alone it does not put a device in the active pool, two weak sources together do.
/// </summary>
public static class ActivityModel
{
    public static IReadOnlyList<ActivitySignal> Signals(Asset a) =>
    [
        new("sccm", "SCCM", a.SccmLastSeenAt, true),
        new("intune", "Intune", a.IntuneLastSyncAt, true),
        new("xdr", "Cortex XDR", a.XdrLastSeenAt, true),
        new("netskope", "Netskope", a.NetskopeLastSeenAt, true),
        new("mam", "Intune MAM", a.MamLastSyncAt, true),
        new("entra", "Entra ID", a.EntraLastSignInAt, false),
        new("ad", "Active Directory", a.AdLastLogonAt, false),
    ];

    public static string ClassOf(int reported, int strong) => reported switch
    {
        >= 2 => ActivityClasses.Confirmed,
        1 when strong == 1 => ActivityClasses.Single,
        1 => ActivityClasses.Unconfirmed,
        _ => ActivityClasses.Inactive,
    };

    public static void Apply(Asset a, DateTimeOffset now, TimeSpan window)
    {
        var signals = Signals(a);
        var inWindow = signals.Where(s => s.At is { } at && now - at <= window).ToList();
        a.ActiveSourceCount = inWindow.Count;
        a.ActiveSources = inWindow.Count == 0 ? null : string.Join(",", inWindow.Select(s => s.Key));
        a.ActivityClass = ClassOf(inWindow.Count, inWindow.Count(s => s.Strong));
        a.LastActivityAt = signals.Max(s => s.At);
        a.LastStrongActivityAt = signals.Where(s => s.Strong).Max(s => s.At);
        a.IsActive = a.ActivityClass is ActivityClasses.Confirmed or ActivityClasses.Single;
    }
}
