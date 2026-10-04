namespace Nexus.Reconciliation;

public sealed record InventoryFilter(string? Group = null, string? Management = null, string? State = null, string? Issue = null,
    string? Ownership = null, string? Funnel = null, string? Query = null, string? Department = null, string? Activity = null);

/// <summary>Filters and sorting behind the full inventory and every drilldown link.</summary>
public static class InventoryQuery
{
    public static readonly string[] Sorts = ["indice", "nome", "grupo", "usuario", "so", "gestao", "estado", "contato", "sccm", "intune", "area", "fontes"];

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

    public static bool Matches(AssetView v, InventoryFilter f, DateTimeOffset now)
    {
        var a = v.Asset;
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
            "contato" => v => v.Asset.LastActivityAt ?? DateTimeOffset.MinValue,
            _ => v => v.Score,
        };
        var ordered = descending ? views.OrderByDescending(key) : views.OrderBy(key);
        return ordered.ThenBy(v => v.Asset.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static string OwnershipOf(AssetView v) => v.Group == Groups.External ? "Externo" : v.Asset.Ownership switch { "Corporate" => "Corporativo", "Personal" => "BYOD", _ => "Desconhecida" };
}
