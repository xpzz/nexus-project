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

/// <summary>How one tool sees the active pool: devices it knows that reported in the window, knows but are silent, or does not know at all.</summary>
public sealed record PoolSource(string Key, string Label, bool Enabled, bool Strong, int Reporting, int Silent, int Absent);

/// <summary>The active pool and how each activity class and each tool contributes to it (ADR-0006).</summary>
public sealed record PoolSummary(int WindowDays, int Pool, int Confirmed, int Single, int Unconfirmed, int Inactive, IReadOnlyList<PoolSource> Sources)
{
    public static PoolSummary Empty { get; } = new(30, 0, 0, 0, 0, 0, []);
}

public sealed record GroupCard(string Group, int Total, int Active, double? Score, int AtRisk, int Stale, int Pending, IReadOnlyDictionary<string, int> ByManagement, IReadOnlyDictionary<string, int> ByState);

public sealed record RuleCount(Rule Rule, int Devices, IReadOnlyDictionary<string, int> ByGroup);

public sealed record OverviewReport(
    double? ParkScore, string Headline, int BelowTarget, double AtRiskPercent,
    IReadOnlyList<KpiCard> Highlights, IReadOnlyList<KpiCard> Kpis, IReadOnlyList<GroupCard> Groups,
    IReadOnlyDictionary<string, int> States, IReadOnlyList<RuleCount> TopRules, int Total, int Active, PoolSummary Pool);

public sealed record FailingPolicy(string Kind, string Name, int Failed, int Total);

/// <summary>Intune in numbers, aggregated in the database: policy catalog, device states and MAM registrations.</summary>
public sealed record IntuneSummary(IReadOnlyDictionary<string, int> PoliciesByKind, int PolicyStates, int FailedStates, int MamRegistrations, int MamUsers, IReadOnlyList<FailingPolicy> TopFailing)
{
    public static IntuneSummary Empty { get; } = new(new Dictionary<string, int>(), 0, 0, 0, 0, []);

    public int Policies => PoliciesByKind.Values.Sum();
}

/// <summary>What the governance collectors brought: app protection, app configuration, Conditional Access, sign-ins, MAM registrations and approved exceptions.</summary>
public sealed record ProtectionData(
    IReadOnlyList<AppProtectionPolicyRecord> Policies, IReadOnlyList<AppConfigRecord> Configs, IReadOnlyList<ConditionalAccessRecord> ConditionalAccess,
    IReadOnlyList<AccessEvidenceRecord> Access, IReadOnlyList<MamRegistrationRecord> Registrations, IReadOnlyList<ProtectionException> Exceptions,
    IReadOnlyList<EntraUserRecord> Users, GovernanceSettingsView Settings)
{
    public static ProtectionData Empty { get; } = new([], [], [], [], [], [], [], new GovernanceSettingsView(1000, 10, 180));
}

/// <summary>The few governance settings the report needs (a view, so the data layer does not depend on the configuration project).</summary>
public sealed record GovernanceSettingsView(int UrlBlocklistLimit, int UrlBlocklistReservePercent, int StalePolicyDays);

public sealed record JobSummary(string Name, string? Status, DateTimeOffset? LastSuccessAt, DateTimeOffset? NextRunAt, int? Records, string? Error);

/// <summary>
/// Immutable result of one reconciliation read. Everything the screens ask repeatedly (counts per group and rule, lookups, the overview)
/// is computed once per snapshot, so a page render never walks the whole inventory more than once.
/// </summary>
public sealed class InventorySnapshot(IReadOnlyList<AssetView> views, IReadOnlyList<ReviewItem> review, IReadOnlyList<JobSummary> jobs, SourceAvailability sources, DateTimeOffset loadedAt, IntuneSummary? intune = null, int windowDays = 30, EvidencePolicy? evidence = null, ProtectionData? protection = null)
{
    private readonly Lazy<Dictionary<Guid, AssetView>> _byId = new(() => views.ToDictionary(v => v.Asset.Id));
    private readonly Lazy<IssueIndex> _issues = new(() => IssueIndex.Build(views));

    public IReadOnlyList<AssetView> Views { get; } = views;
    public IReadOnlyList<ReviewItem> Review { get; } = review;
    public IReadOnlyList<JobSummary> Jobs { get; } = jobs;
    public SourceAvailability Sources { get; } = sources;
    public DateTimeOffset LoadedAt { get; } = loadedAt;
    public IntuneSummary Intune { get; } = intune ?? IntuneSummary.Empty;
    public int WindowDays { get; } = windowDays;
    public ProtectionData Protection { get; } = protection ?? ProtectionData.Empty;
    public EvidencePolicy Evidence { get; } = evidence ?? EvidencePolicy.FromWindow(TimeSpan.FromDays(windowDays));

    public static InventorySnapshot Empty { get; } = new([], [], [], new SourceAvailability(false, false, false, false), DateTimeOffset.MinValue);

    public bool HasData => Views.Count > 0;

    public IssueIndex Issues => _issues.Value;

    public AssetView? Find(Guid id) => _byId.Value.GetValueOrDefault(id);

    public OverviewReport Overview() => _overviewCache ??= OverviewBuilder.Build(this);

    public MamReport Mam() => _mamCache ??= MamReport.Build(this);

    public ExecutiveReport Executive() => _executiveCache ??= ExecutiveSummary.Build(this);

    public SourceComparisonReport Comparison() => _comparisonCache ??= SourceComparison.Build(Views, Sources, LoadedAt, WindowDays);

    private OverviewReport? _overviewCache;
    private SourceComparisonReport? _comparisonCache;
    private ExecutiveReport? _executiveCache;
    private MamReport? _mamCache;
}

/// <summary>Counts per group, per rule and per rule × group, from a single pass.</summary>
public sealed class IssueIndex
{
    public IReadOnlyDictionary<string, int> Groups { get; private init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Rules { get; private init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<(string Group, string Rule), int> RulesByGroup { get; private init; } = new Dictionary<(string, string), int>();
    public int WithPending { get; private init; }

    public int Group(string group) => Groups.GetValueOrDefault(group);

    public int Rule(string rule) => Rules.GetValueOrDefault(rule);

    public int RuleInGroup(string group, string rule) => RulesByGroup.GetValueOrDefault((group, rule));

    public static IssueIndex Build(IReadOnlyList<AssetView> views)
    {
        var groups = new Dictionary<string, int>();
        var rules = new Dictionary<string, int>();
        var byGroup = new Dictionary<(string, string), int>();
        var pending = 0;
        foreach (var v in views)
        {
            groups[v.Group] = groups.GetValueOrDefault(v.Group) + 1;
            var any = false;
            foreach (var issue in v.Issues)
            {
                rules[issue] = rules.GetValueOrDefault(issue) + 1;
                byGroup[(v.Group, issue)] = byGroup.GetValueOrDefault((v.Group, issue)) + 1;
                any |= issue != "stale";
            }

            if (any)
            {
                pending++;
            }
        }

        return new IssueIndex { Groups = groups, Rules = rules, RulesByGroup = byGroup, WithPending = pending };
    }
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

    private static readonly (string Key, string Label, bool Strong, Func<Asset, bool> Known, Func<SourceAvailability, bool> Enabled)[] PoolSources =
    [
        ("sccm", "SCCM", true, a => a.InSccm, x => x.Sccm),
        ("intune", "Intune", true, a => a.InIntune, x => x.Intune),
        ("xdr", "Cortex XDR", true, a => a.InXdr, x => x.Xdr),
        ("netskope", "Netskope", true, a => a.InNetskope, x => x.Netskope),
        ("mam", "Intune MAM", true, a => a.HasMam, x => x.Mam),
        ("entra", "Entra ID", false, a => a.InEntra, x => x.Entra),
        ("ad", "Active Directory", false, a => a.InAd, x => x.ActiveDirectory),
    ];

    private static PoolSummary BuildPool(IReadOnlyList<AssetView> views, SourceAvailability src, int windowDays)
    {
        var pool = views.Where(v => v.Asset.IsActive).ToList();
        var sources = PoolSources.Select(p =>
        {
            var reporting = pool.Count(v => (v.Asset.ActiveSources ?? "").Split(',').Contains(p.Key));
            var known = pool.Count(v => p.Known(v.Asset));
            return new PoolSource(p.Key, p.Label, p.Enabled(src), p.Strong, reporting, Math.Max(0, known - reporting), pool.Count - Math.Max(known, reporting));
        }).ToList();
        return new PoolSummary(windowDays, pool.Count, views.Count(v => v.Asset.ActivityClass == ActivityClasses.Confirmed), views.Count(v => v.Asset.ActivityClass == ActivityClasses.Single),
            views.Count(v => v.Asset.ActivityClass == ActivityClasses.Unconfirmed), views.Count(v => v.Asset.ActivityClass == ActivityClasses.Inactive), sources);
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
        var winEncryptable = active.Where(v => v is { Group: Groups.Computers } && v.Asset is { Platform: "WindowsClient", IntuneChannel: "Mdm", IsEncrypted: not null }).ToList();

        var highlights = new List<KpiCard>
        {
            Make("gestao", "Cobertura de gestão", anyMgmt, corporate.Count(v => v.Management != Management.None), corporate.Count, 98, "corporativos ativos com alguma gestão", "SCCM ou Intune ainda sem coleta"),
            Make("conformidade", "Conformidade Intune", src.Intune, mdm.Count(v => string.Equals(v.Asset.ComplianceState, "compliant", StringComparison.OrdinalIgnoreCase)), mdm.Count, 95, "dispositivos MDM ativos conformes", "Intune ainda sem coleta"),
            Make("patch", "Patch em até 30 dias", false, 0, 0, 90, "", "Estado de atualizações ainda não é coletado"),
            Make("edr", "Cobertura de EDR (Cortex XDR)", src.Xdr && winCorp.Count > 0, winCorp.Count(v => v.Asset.InXdr && Reconciler.IsXdrConnected(v.Asset.XdrStatus)), winCorp.Count, 98,
                "Windows corporativos ativos com agente XDR conectado", "Cortex XDR não configurado (nexusctl xdr-configure)"),
            Make("cripto", "Criptografia (BitLocker)", src.Intune && winEncryptable.Count > 0, winEncryptable.Count(v => v.Asset.IsEncrypted == true), winEncryptable.Count, 98,
                "Windows MDM ativos com BitLocker ligado (EDR ainda não é coletado)", "Intune ainda sem estado de criptografia"),
            Make("byod", "BYOD protegido", src.Intune && src.Mam, byod.Count(v => v.Asset.IntuneChannel == "Mdm" || v.Asset.HasMam), byod.Count, 90,
                "BYOD ativos com MDM ou proteção de aplicativos (MAM)", "Proteção de aplicativos (MAM) ainda sem coleta"),
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
            new("politicas", "Perfis ou políticas com falha", src.Policies ? KpiState.Available : KpiState.NotEnabled, views.Count(v => v.Asset.CompliancePoliciesFailed > 0 || v.Asset.ConfigProfilesFailed > 0), views.Count(v => v.Asset.PoliciesCollected), null, true, "dispositivos com política ou perfil em erro, conflito ou não conforme", "/inventario?pendencia=cfgfail"),
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

        var index = s.Issues;
        var top = HealthModel.Rules.Where(r => r.IsAvailable(s.Sources) && r.Id != "stale")
            .Select(r => new RuleCount(r, index.Rule(r.Id), Groups.All.ToDictionary(g => g, g => index.RuleInGroup(g, r.Id))))
            .Where(r => r.Devices > 0).OrderBy(r => r.Rule.Priority).ThenByDescending(r => r.Devices).Take(6).ToList();

        var pool = BuildPool(views, src, s.WindowDays);
        return new OverviewReport(active.Count == 0 ? null : Math.Round(active.Average(v => v.Score), 0), headline, below, atRiskPct, highlights, kpis, groups, states, top, views.Count, active.Count, pool);
    }
}

public static class InventorySnapshotLoader
{
    public static async Task<InventorySnapshot> LoadAsync(INexusDbFactory dbFactory, TimeProvider clock, CancellationToken cancellationToken, EvidencePolicy? policy = null, GovernanceSettingsView? governance = null)
    {
        policy ??= EvidencePolicy.FromWindow(TimeSpan.FromDays(30));
        await using var db = dbFactory.Create();
        var sources = await InventoryReports.AvailabilityAsync(db, cancellationToken);
        var assets = await db.Assets.AsNoTracking().ToListAsync(cancellationToken);
        var review = await db.ReviewItems.AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken);
        var jobs = (await db.Jobs.AsNoTracking().ToListAsync(cancellationToken))
            .Select(j => new JobSummary(j.Name, j.LastStatus, j.LastSuccessAt, j.NextRunAt, j.LastRecordCount, j.LastError)).OrderBy(j => j.Name).ToList();
        var kinds = await db.IntunePolicies.AsNoTracking().GroupBy(p => p.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var states = await db.IntuneDevicePolicyStates.AsNoTracking().GroupBy(p => new { p.Kind, p.PolicyName, p.State }).Select(g => new { g.Key.Kind, g.Key.PolicyName, g.Key.State, Count = g.Count() }).ToListAsync(cancellationToken);
        var failing = states.GroupBy(x => (x.Kind, x.PolicyName))
            .Select(g => new FailingPolicy(g.Key.Kind, g.Key.PolicyName, g.Where(x => Reconciler.IsFailed(x.State)).Sum(x => x.Count), g.Sum(x => x.Count)))
            .Where(f => f.Failed > 0).OrderByDescending(f => f.Failed).Take(5).ToList();
        var mamCount = await db.MamRegistrations.CountAsync(cancellationToken);
        var mamUsers = await db.MamRegistrations.Where(r => r.UserId != null).Select(r => r.UserId).Distinct().CountAsync(cancellationToken);
        var intune = new IntuneSummary(kinds.ToDictionary(k => k.Key, k => k.Count), states.Sum(x => x.Count), states.Where(x => Reconciler.IsFailed(x.State)).Sum(x => x.Count), mamCount, mamUsers, failing);
        var protection = new ProtectionData(
            await db.AppProtectionPolicies.AsNoTracking().ToListAsync(cancellationToken), await db.AppConfigs.AsNoTracking().ToListAsync(cancellationToken),
            await db.ConditionalAccessPolicies.AsNoTracking().ToListAsync(cancellationToken), await db.AccessEvidence.AsNoTracking().ToListAsync(cancellationToken),
            await db.MamRegistrations.AsNoTracking().ToListAsync(cancellationToken), (await db.ProtectionExceptions.AsNoTracking().ToListAsync(cancellationToken)).Where(e => e.Active).ToList(),
            await db.EntraUsers.AsNoTracking().ToListAsync(cancellationToken), governance ?? ProtectionData.Empty.Settings);
        return new InventorySnapshot(assets.Select(a => AssetView.From(a, sources)).ToList(), review, jobs, sources, clock.GetUtcNow(), intune, policy.Default.ProbableDays, policy, protection);
    }
}

/// <summary>
/// Serves the last snapshot immediately and refreshes it in the background once it is older than a minute (stale-while-revalidate),
/// so no page waits for the database after the first load. Collections run every 15 to 240 minutes, so a minute of staleness is invisible.
/// </summary>
public sealed class InventorySnapshotService(INexusDbFactory dbFactory, TimeProvider clock, Func<EvidencePolicy>? policy = null, Func<GovernanceSettingsView>? governance = null)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile InventorySnapshot _snapshot = InventorySnapshot.Empty;
    private int _refreshing;

    public async Task<InventorySnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var current = _snapshot;
        if (current.LoadedAt == DateTimeOffset.MinValue)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_snapshot.LoadedAt == DateTimeOffset.MinValue)
                {
                    _snapshot = await InventorySnapshotLoader.LoadAsync(dbFactory, clock, cancellationToken, policy?.Invoke(), governance?.Invoke());
                }
            }
            finally
            {
                _lock.Release();
            }

            return _snapshot;
        }

        if (clock.GetUtcNow() - current.LoadedAt > Ttl && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _snapshot = await InventorySnapshotLoader.LoadAsync(dbFactory, clock, CancellationToken.None, policy?.Invoke(), governance?.Invoke());
                }
                catch (Exception)
                {
                    // Keep serving the last good snapshot; the next request tries again.
                }
                finally
                {
                    Volatile.Write(ref _refreshing, 0);
                }
            });
        }

        return current;
    }
}
