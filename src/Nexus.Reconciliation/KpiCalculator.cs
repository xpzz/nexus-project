using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

public enum KpiState
{
    Available,
    NotEnabled,
    NoData,
}

/// <summary>A KPI always carries its numerator, denominator and eligible population (SPEC §9).</summary>
public sealed record Kpi(string Key, string Label, string Definition, KpiState State, int? Numerator, int? Denominator, string? Note)
{
    public double? Percent => State == KpiState.Available && Denominator is > 0 ? Math.Round(100.0 * Numerator!.Value / Denominator.Value, 1) : null;
}

public sealed record SourceAvailability(bool Sccm, bool Intune, bool Entra, bool ActiveDirectory, bool Policies = false, bool Mam = false, bool Users = false, bool Xdr = false, bool Netskope = false)
{
    public static SourceAvailability All { get; } = new(true, true, true, true, true, true, true, true, true);
}

public sealed record InventoryCounts(int Total, int Active, int Stale, int OnlySccm, int OnlyIntune, int Both, int Neither,
    int SccmWithoutClient, int Review, int PersonalDevices, int CorporateMobile);

public sealed record KpiReport(IReadOnlyList<Kpi> Kpis, InventoryCounts Counts);

/// <summary>
/// Coverage KPIs with explicit denominators (SPEC §8.1/9.2). Only active corporate devices that are expected to use a channel
/// are counted; stale devices, personal devices and review items are reported separately and never hide inside the percentage.
/// A source that is not available turns its KPIs into "não habilitado"/"sem dados" instead of a misleading zero.
/// </summary>
public static class KpiCalculator
{
    public static KpiReport Calculate(IReadOnlyCollection<Asset> assets, SourceAvailability sources)
    {
        var active = assets.Where(a => a.IsActive).ToList();
        var winClients = active.Where(a => a is { Platform: "WindowsClient", Ownership: "Corporate" }).ToList();
        var winServers = active.Where(a => a is { Platform: "WindowsServer", Ownership: "Corporate" }).ToList();
        var shouldUseSccm = winClients.Concat(winServers).ToList();

        var kpis = new List<Kpi>
        {
            Build("sccm-coverage", "Cobertura SCCM",
                "Windows corporativo ativo com cliente SCCM confirmado ÷ Windows corporativo ativo (clientes e servidores)",
                sources.Sccm, sources.Sccm, shouldUseSccm.Count(a => a.SccmClient), shouldUseSccm.Count,
                "Dispositivos obsoletos no SCCM não contam como cliente."),
            Build("sccm-healthy", "Cobertura SCCM saudável",
                "Windows corporativo ativo com cliente saudável ÷ Windows corporativo ativo",
                sources.Sccm, sources.Sccm, shouldUseSccm.Count(a => a.SccmHealth == "Healthy"), shouldUseSccm.Count, null),
            Build("intune-mdm-coverage", "Cobertura Intune MDM",
                "Windows cliente corporativo ativo com enrollment MDM confirmado ÷ Windows cliente corporativo ativo",
                sources.Intune, sources.Intune, winClients.Count(a => a.IntuneChannel == "Mdm"), winClients.Count,
                "Tenant attach e Defender não contam como MDM; servidores não são exigidos."),
            Build("complete-coverage", "Cobertura completa",
                "Windows cliente corporativo ativo com SCCM e Intune MDM ÷ Windows cliente corporativo ativo",
                sources.Sccm && sources.Intune, sources.Sccm && sources.Intune, winClients.Count(a => a.Coverage == "Both"), winClients.Count, null),
            Build("mobile-corporate", "Celulares corporativos com MDM",
                "Android/iOS corporativo ativo com MDM ÷ Android/iOS corporativo ativo",
                sources.Intune, sources.Intune,
                active.Count(a => a is { Platform: "Android" or "iOS", Ownership: "Corporate", IntuneChannel: "Mdm" }),
                active.Count(a => a is { Platform: "Android" or "iOS", Ownership: "Corporate" }), null),
        };

        var counts = new InventoryCounts(
            assets.Count, active.Count, assets.Count - active.Count,
            active.Count(a => a.Coverage == "OnlySccm"), active.Count(a => a.Coverage == "OnlyIntune"),
            active.Count(a => a.Coverage == "Both"), active.Count(a => a.Coverage == "Neither"),
            active.Count(a => a.InSccm && a.SccmHealth == "NoClient"), assets.Count(a => a.NeedsReview),
            assets.Count(a => a.Ownership == "Personal"),
            assets.Count(a => a is { Platform: "Android" or "iOS", Ownership: "Corporate" }));
        return new KpiReport(kpis, counts);
    }

    private static Kpi Build(string key, string label, string definition, bool enabled, bool hasData, int numerator, int denominator, string? note)
    {
        if (!enabled)
        {
            return new Kpi(key, label, definition, KpiState.NotEnabled, null, null, "A fonte necessária não está habilitada ou ainda não coletou.");
        }

        return denominator == 0 && hasData
            ? new Kpi(key, label, definition, KpiState.NoData, null, null, "Nenhum dispositivo elegível encontrado.")
            : new Kpi(key, label, definition, KpiState.Available, numerator, denominator, note);
    }
}
