using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

public sealed record InventoryFilter(string? Group = null, string? Management = null, string? State = null, string? Issue = null,
    string? Ownership = null, string? Funnel = null, string? Query = null, string? Department = null, string? Activity = null,
    string? Oper = null, string? AssetType = null, string? Flags = null);

/// <summary>Filters and sorting behind the full inventory and every drilldown link.</summary>
public static class InventoryQuery
{
    public static readonly string[] Sorts = ["indice", "nome", "grupo", "usuario", "so", "gestao", "estado", "contato", "sccm", "intune", "area", "fontes", "tipo", "oper", "score", "evidencia", "serial", "propriedade", "atencao"];

    public static readonly IReadOnlyDictionary<string, string> FunnelNames = new Dictionary<string, string>
    {
        ["sccm-sem-cliente"] = "Descobertos no SCCM sem cliente instalado",
        ["sccm-silencioso"] = "Cliente SCCM sem reportar há mais de 30 dias",
        ["sccm-nao-saudavel"] = "Cliente SCCM instalado e não saudável",
        ["xdr-sem-agente"] = "Windows corporativo ativo sem agente Cortex XDR",
        ["xdr-desconectado"] = "Agente XDR que não está conectado",
        ["xdr-silencioso"] = "Agente XDR sem reportar há mais de 7 dias",
        ["intune-fora-entra"] = "Corporativos fora do Entra ID",
        ["intune-sem-mdm"] = "No Entra ID e sem Intune MDM",
        ["intune-sem-sync"] = "Intune MDM sem sincronizar há mais de 7 dias",
        ["intune-nao-conforme"] = "Intune MDM sem estar conforme",
    };

    /// <summary>Boolean facts that can be combined in the inventory (<c>flag=a,b</c>: all must hold).</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, Func<AssetView, bool> Test)> Flags = new Dictionary<string, (string, Func<AssetView, bool>)>
    {
        ["candidato"] = ("Candidato à inativação", v => v.Asset.DecommissionCandidate),
        ["entra"] = ("Registrado no Entra ID", v => v.Asset.InEntra),
        ["mdm"] = ("Gerenciado por MDM", v => v.Asset.IntuneChannel == "Mdm"),
        ["naoconforme"] = ("Não compliant no Intune", v => v.Asset.IntuneChannel == "Mdm" && !string.Equals(v.Asset.ComplianceState, "compliant", StringComparison.OrdinalIgnoreCase)),
        ["mam"] = ("Com proteção de apps (MAM)", v => v.Asset.HasMam),
        ["semmam"] = ("Sem proteção de apps (MAM)", v => !v.Asset.HasMam),
        ["xdr"] = ("Com agente Cortex XDR", v => v.Asset.InXdr),
        ["semserial"] = ("Sem serial válido", v => string.IsNullOrWhiteSpace(v.Asset.Serial)),
        ["semusuario"] = ("Sem usuário associado", v => string.IsNullOrWhiteSpace(v.Asset.PrimaryUser) && string.IsNullOrWhiteSpace(v.Asset.LastUser)),
        ["fonteunica"] = ("Encontrado em uma única fonte", v => SourceCount(v.Asset) == 1),
        ["identidadefraca"] = ("Identidade só por nome ou confiança baixa", v => v.Asset.Confidence == "Low" || v.Asset.AdByNameOnly || v.Asset.XdrByNameOnly || v.Asset.NetskopeByNameOnly),
        ["revisao"] = ("Reconciliação a revisar", v => v.Asset.NeedsReview),
    };

    public static int SourceCount(Asset a) => new[] { a.InSccm, a.InIntune, a.InEntra, a.InAd, a.InXdr, a.InNetskope, a.HasMam }.Count(x => x);

    public static string? FlagLabel(string flag) => Flags.TryGetValue(flag, out var f) ? f.Label : null;

    public static IEnumerable<string> Split(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Matches(AssetView v, InventoryFilter f, DateTimeOffset now)
    {
        var a = v.Asset;
        if (f.Oper is { Length: > 0 } oper && !Split(oper).Contains(a.OperationalState))
        {
            return false;
        }

        if (f.AssetType is { Length: > 0 } type && !Split(type).Contains(a.AssetType))
        {
            return false;
        }

        if (f.Flags is { Length: > 0 } flags && Split(flags).Any(x => !Flags.TryGetValue(x, out var fl) || !fl.Test(v)))
        {
            return false;
        }

        if (f.Group is { Length: > 0 } g && !(g == "byod" ? v.Group is Groups.ByodMobile or Groups.ByodComputers : g == "sem-gestao" ? v.Issues.Contains("nomgr") : v.Group == g))
        {
            return false;
        }

        if (f.Management is { Length: > 0 } m && v.Management != m)
        {
            return false;
        }

        if (f.State is { Length: > 0 } s && !(s == "ativos" ? a.IsActive : v.State == s))
        {
            return false;
        }

        if (f.Issue is { Length: > 0 } i && !v.Issues.Contains(i))
        {
            return false;
        }

        if (f.Ownership is { Length: > 0 } o && OwnershipOf(v) != o)
        {
            return false;
        }

        if (f.Department is { Length: > 0 } dep && !string.Equals(a.Department, dep, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (f.Activity is { Length: > 0 } act && !(act == "pool" ? a.IsActive : a.ActivityClass == act))
        {
            return false;
        }

        if (f.Funnel is { Length: > 0 } fn && !Funnel(v, fn, now))
        {
            return false;
        }

        if (f.Query is { Length: > 0 } q)
        {
            var hay = $"{a.Name} {a.PrimaryUser} {a.Serial} {a.Model} {a.Manufacturer} {a.OperatingSystem}";
            if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool Funnel(AssetView v, string name, DateTimeOffset now)
    {
        var a = v.Asset;
        if (name.Contains(':'))
        {
            return SourceComparison.Matches(a, name);
        }

        var pcOrMobile = v.Group is Groups.Computers or Groups.CorpMobile;
        return name switch
        {
            "sccm-sem-cliente" => a is { InSccm: true, SccmClient: false } && a.SccmHealth != "Obsolete",
            "sccm-silencioso" => a.SccmClient && (a.SccmLastSeenAt is null || now - a.SccmLastSeenAt > TimeSpan.FromDays(30)),
            "sccm-nao-saudavel" => a.SccmClient && a.SccmHealth != "Healthy",
            "xdr-sem-agente" => a.Ownership == "Corporate" && a.IsActive && !a.InXdr && v.Group is Groups.Computers or Groups.Servers && a.Platform is "WindowsClient" or "WindowsServer",
            "xdr-desconectado" => a.InXdr && !Reconciler.IsXdrConnected(a.XdrStatus),
            "xdr-silencioso" => a.InXdr && (a.XdrLastSeenAt is null || now - a.XdrLastSeenAt > TimeSpan.FromDays(7)),
            "intune-fora-entra" => pcOrMobile && !a.InEntra,
            "intune-sem-mdm" => pcOrMobile && a.InEntra && a.IntuneChannel != "Mdm",
            "intune-sem-sync" => a.IntuneChannel == "Mdm" && (a.IntuneLastSyncAt is null || now - a.IntuneLastSyncAt > TimeSpan.FromDays(7)),
            "intune-nao-conforme" => a.IntuneChannel == "Mdm" && !string.Equals(a.ComplianceState, "compliant", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    public static IEnumerable<AssetView> Sort(IEnumerable<AssetView> views, string? sort, bool descending)
    {
        Func<AssetView, object> key = sort switch
        {
            "nome" => v => v.Asset.Name.ToLowerInvariant(),
            "grupo" => v => v.Group,
            "usuario" => v => (v.Asset.PrimaryUser ?? "").ToLowerInvariant(),
            "so" => v => v.Asset.OperatingSystem ?? "",
            "gestao" => v => v.Management,
            "sccm" => v => v.Asset.SccmLastSeenAt ?? DateTimeOffset.MinValue,
            "intune" => v => v.Asset.IntuneLastSyncAt ?? DateTimeOffset.MinValue,
            "fontes" => v => v.Asset.ActiveSourceCount,
            "area" => v => (v.Asset.Department ?? "").ToLowerInvariant(),
            "estado" => v => States.All.ToList().IndexOf(v.State),
            "contato" or "evidencia" => v => v.Asset.LastActivityAt ?? DateTimeOffset.MinValue,
            "tipo" => v => v.Asset.AssetType,
            "oper" => v => OperationalStates.All.ToList().IndexOf(v.Asset.OperationalState),
            "score" => v => v.Asset.ActivityScore,
            "serial" => v => (v.Asset.Serial ?? "~").ToLowerInvariant(),
            "propriedade" => v => OwnershipOf(v),
            "atencao" => v => v.Issues.Count(i => i != "stale"),
            _ => v => v.Score,
        };
        var ordered = descending ? views.OrderByDescending(key) : views.OrderBy(key);
        return ordered.ThenBy(v => v.Asset.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static string OwnershipOf(AssetView v) => v.Group == Groups.External ? "Externo" : v.Asset.Ownership switch { "Corporate" => "Corporativo", "Personal" => "BYOD", _ => "Desconhecida" };
}
