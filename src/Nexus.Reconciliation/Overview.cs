using Microsoft.EntityFrameworkCore;
using Nexus.Data;
using Nexus.Data.Entities;
using Nexus.Data.Support;

namespace Nexus.Reconciliation;

/// <summary>A KPI with its target (SPEC v1 §7). Unavailable KPIs say why instead of showing zero.</summary>
public sealed record KpiCard(string Key, string Label, KpiState State, int? Numerator, int? Denominator, double? Target, bool LowerIsBetter, string Context, string? Link = null)
{
    public double? Percent => State == KpiState.Available && Denominator is > 0 && Target is not null ? Math.Round(100.0 * Numerator!.Value / Denominator.Value, 1) : null;

    public bool MeetsTarget => State == KpiState.Available && Target is { } t && (Percent ?? 0) >= t;

    /// <summary>Percentage points missing to reach the target (0 when reached).</summary>
    public double? MissingPoints => Percent is { } p && Target is { } t ? Math.Max(0, Math.Round(t - p, 1)) : null;
}

public sealed record GroupCard(string Group, int Total, int Active, double? Score, int AtRisk, int Stale, int Pending, IReadOnlyDictionary<string, int> ByManagement, IReadOnlyDictionary<string, int> ByState);

public sealed record RuleCount(Rule Rule, int Devices, IReadOnlyDictionary<string, int> ByGroup);

public sealed record OverviewReport(
    double? ParkScore, string Headline, int BelowTarget, double AtRiskPercent,
    IReadOnlyList<KpiCard> Highlights, IReadOnlyList<KpiCard> Kpis, IReadOnlyList<GroupCard> Groups,
    IReadOnlyDictionary<string, int> States, IReadOnlyList<RuleCount> TopRules, int Total, int Active);

public sealed record JobSummary(string Name, string? Status, DateTimeOffset? LastSuccessAt, DateTimeOffset? NextRunAt, int? Records, string? Error);

public sealed record InventorySnapshot(IReadOnlyList<AssetView> Views, IReadOnlyList<ReviewItem> Review, IReadOnlyList<JobSummary> Jobs, SourceAvailability Sources, DateTimeOffset LoadedAt)
{
    public static InventorySnapshot Empty { get; } = new([], [], [], new SourceAvailability(false, false, false, false), DateTimeOffset.MinValue);

    public bool HasData => Views.Count > 0;

    public AssetView? Find(Guid id) => Views.FirstOrDefault(v => v.Asset.Id == id);

    public OverviewReport Overview() => OverviewBuilder.Build(this);
}

public static class OverviewBuilder
{
    private static KpiCard Make(string key, string label, bool enabled, int num, int den, double target, string context, string? missing = null, bool lower = false, string? link = null)
    {
        if (!enabled)
        {
            return new KpiCard(key, label, KpiState.NotEnabled, null, null, target, lower, missing ?? "Fonte necessária não habilitada.", link);
        }

        return den == 0
            ? new KpiCard(key, label, KpiState.NoData, null, null, target, lower, "Nenhum dispositivo elegível.", link)
            : new KpiCard(key, label, KpiState.Available, num, den, target, lower, context, link);
    }

    public static OverviewReport Build(InventorySnapshot s)
    {
        var views = s.Views;
        var src = s.Sources;
        var active = views.Where(v => v.Asset.IsActive).ToList();
        var corporate = active.Where(v => v.Asset.Ownership == "Corporate" && v.Group is Groups.Computers or Groups.Servers or Groups.CorpMobile).ToList();
        var winClients = active.Where(v => v is { Group: Groups.Computers } && v.Asset.Platform == "WindowsClient").ToList();
        var winCorp = active.Where(v => v.Group is Groups.Computers or Groups.Servers && v.Asset.Platform is "WindowsClient" or "WindowsServer").ToList();
        var mdm = active.Where(v => v.Asset.IntuneChannel == "Mdm").ToList();
        var byod = active.Where(v => v.Group is Groups.ByodMobile or Groups.ByodComputers).ToList();
        var anyMgmt = src.Sccm || src.Intune;

        var highlights = new List<KpiCard>
        {
            Make("gestao", "Cobertura de gestão", anyMgmt, corporate.Count(v => v.Management != Management.None), corporate.Count, 98, "corporativos ativos com alguma gestão", "SCCM ou Intune ainda sem coleta"),
            Make("conformidade", "Conformidade Intune", src.Intune, mdm.Count(v => string.Equals(v.Asset.ComplianceState, "compliant", StringComparison.OrdinalIgnoreCase)), mdm.Count, 95, "dispositivos MDM ativos conformes", "Intune ainda sem coleta"),
            Make("patch", "Patch em até 30 dias", false, 0, 0, 90, "", "Estado de atualizações ainda não é coletado"),
            Make("cripto", "Criptografia e EDR", false, 0, 0, 98, "", "BitLocker e Defender ainda não são coletados"),
            Make("byod", "BYOD protegido", false, 0, 0, 90, "", "Proteção de aplicativos (MAM) ainda não é coletada"),
        };

        var cogestao = Make("cogestao", "Co-gestão Windows", src.Sccm && src.Intune, winClients.Count(v => v.Management == Management.CoManaged), winClients.Count, 95, "Windows ativos com SCCM e Intune MDM");
        var healthy = Make("sccm-saudavel", "Cliente SCCM saudável", src.Sccm, winCorp.Count(v => v.Asset.SccmHealth == "Healthy"), winCorp.Count, 97, "Windows corporativos ativos com cliente saudável");
        var kpis = new List<KpiCard>
        {
            new("ativos", "Dispositivos ativos", KpiState.Available, active.Count, views.Count, null, false, $"de {views.Count} no inventário", "/inventario?estado=ativos"),
            cogestao,
            healthy,
            new("semgestao", "Corporativos sem gestão", anyMgmt ? KpiState.Available : KpiState.NotEnabled, views.Count(v => v.Issues.Contains("nomgr")), views.Count, null, true, "no AD ou Entra, fora do SCCM e do Intune", "/inventario?pendencia=nomgr"),
            new("win10", "Windows 10 restantes", KpiState.Available, views.Count(v => v.Issues.Contains("eol")), views.Count, null, true, "fora de suporte desde out/2025", "/inventario?pendencia=eol"),
            new("altas", "Pendências críticas e altas", KpiState.Available, views.Count(v => v.State == States.Risk), views.Count, null, true, "dispositivos em risco", "/inventario?estado=Risco"),
        };

        var states = States.All.ToDictionary(x => x, x => views.Count(v => v.State == x));
        var atRiskPct = active.Count == 0 ? 0 : Math.Round(100.0 * active.Count(v => v.State == States.Risk) / active.Count, 1);
        var below = highlights.Concat([cogestao, healthy]).Count(k => k.State == KpiState.Available && !k.MeetsTarget);
        var headline = below >= 3 || atRiskPct >= 20 ? "Pede ação" : below >= 1 || atRiskPct >= 8 ? "Estável" : "Saudável";

        var groups = Groups.All.Select(g =>
        {
            var all = views.Where(v => v.Group == g).ToList();
            var act = all.Where(v => v.Asset.IsActive).ToList();
            return new GroupCard(g, all.Count, act.Count, act.Count == 0 ? null : Math.Round(act.Average(v => v.Score), 0),
                act.Count(v => v.State == States.Risk), all.Count - act.Count, all.Count(v => v.Issues.Any(i => i != "stale")),
                Management.All.ToDictionary(m => m, m => all.Count(v => v.Management == m)), States.All.ToDictionary(x => x, x => all.Count(v => v.State == x)));
        }).ToList();

        var top = HealthModel.Rules.Where(r => r.Available && r.Id != "stale")
            .Select(r => new RuleCount(r, views.Count(v => v.Issues.Contains(r.Id)), Groups.All.ToDictionary(g => g, g => views.Count(v => v.Group == g && v.Issues.Contains(r.Id)))))
            .Where(r => r.Devices > 0).OrderBy(r => r.Rule.Priority).ThenByDescending(r => r.Devices).Take(6).ToList();

        return new OverviewReport(active.Count == 0 ? null : Math.Round(active.Average(v => v.Score), 0), headline, below, atRiskPct, highlights, kpis, groups, states, top, views.Count, active.Count);
    }
}

public static class InventorySnapshotLoader
{
    public static async Task<InventorySnapshot> LoadAsync(INexusDbFactory dbFactory, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = dbFactory.Create();
        var sources = await InventoryReports.AvailabilityAsync(db, cancellationToken);
        var assets = await db.Assets.AsNoTracking().ToListAsync(cancellationToken);
        var review = await db.ReviewItems.AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken);
        var jobs = (await db.Jobs.AsNoTracking().ToListAsync(cancellationToken))
            .Select(j => new JobSummary(j.Name, j.LastStatus, j.LastSuccessAt, j.NextRunAt, j.LastRecordCount, j.LastError)).OrderBy(j => j.Name).ToList();
        return new InventorySnapshot(assets.Select(a => AssetView.From(a, sources)).ToList(), review, jobs, sources, clock.GetUtcNow());
    }
}

/// <summary>Caches the snapshot for a short time so every screen shares one query; collections run every 15 to 240 minutes.</summary>
public sealed class InventorySnapshotService(INexusDbFactory dbFactory, TimeProvider clock)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private InventorySnapshot _snapshot = InventorySnapshot.Empty;

    public async Task<InventorySnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        if (clock.GetUtcNow() - _snapshot.LoadedAt < Ttl)
        {
            return _snapshot;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (clock.GetUtcNow() - _snapshot.LoadedAt >= Ttl)
            {
                _snapshot = await InventorySnapshotLoader.LoadAsync(dbFactory, clock, cancellationToken);
            }

            return _snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }
}
