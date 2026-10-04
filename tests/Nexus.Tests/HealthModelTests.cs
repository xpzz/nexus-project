using Nexus.Data.Entities;
using Nexus.Reconciliation;

namespace Nexus.Tests;

public class HealthModelTests
{
    private static readonly SourceAvailability All = SourceAvailability.All;

    private static Asset Win(string coverage = "Both", string os = "Microsoft Windows 11 Enterprise", string version = "10.0.22631", bool active = true) => new()
    {
        Id = Guid.NewGuid(), Name = "PC", Platform = "WindowsClient", Ownership = "Corporate", OperatingSystem = os, OsVersion = version, IsActive = active, ComplianceState = "compliant",
        Coverage = coverage, InSccm = coverage is "Both" or "OnlySccm", SccmClient = coverage is "Both" or "OnlySccm", SccmHealth = coverage is "Both" or "OnlySccm" ? "Healthy" : "NotApplicable",
        InIntune = coverage is "Both" or "OnlyIntune", IntuneChannel = coverage is "Both" or "OnlyIntune" ? "Mdm" : "None", InAd = true,
    };

    [Fact]
    public void GroupsFollowOwnershipPlatformAndSources()
    {
        Assert.Equal(Groups.Computers, HealthModel.GroupOf(Win()));
        Assert.Equal(Groups.Servers, HealthModel.GroupOf(new Asset { Platform = "WindowsServer", Ownership = "Corporate", InSccm = true }));
        Assert.Equal(Groups.CorpMobile, HealthModel.GroupOf(new Asset { Platform = "iOS", Ownership = "Corporate", InIntune = true }));
        Assert.Equal(Groups.ByodMobile, HealthModel.GroupOf(new Asset { Platform = "Android", Ownership = "Personal", InIntune = true }));
        Assert.Equal(Groups.ByodComputers, HealthModel.GroupOf(new Asset { Platform = "WindowsClient", Ownership = "Personal", InIntune = true }));
        Assert.Equal(Groups.External, HealthModel.GroupOf(new Asset { Platform = "WindowsClient", Ownership = "Personal", InEntra = true }));
    }

    [Theory]
    [InlineData("10.0.19045", "Windows", true)]
    [InlineData("10.0.22631", "Windows", false)]
    [InlineData(null, "Microsoft Windows 10 Enterprise", true)]
    [InlineData(null, "Microsoft Windows 11 Enterprise", false)]
    public void Windows10IsDetectedByBuildThenByName(string? version, string os, bool expected) =>
        Assert.Equal(expected, HealthModel.IsWindows10(Win(os: os, version: version!)));

    [Fact]
    public void ACoManagedHealthyDeviceHasNoIssuesAndFullScore()
    {
        var view = AssetView.From(Win(), All);
        Assert.Empty(view.Issues);
        Assert.Equal((100, States.Healthy, Management.CoManaged), (view.Score, view.State, view.Management));
    }

    [Fact]
    public void SccmOnlyWindowsClientMissesMdmButMacOnlyNeedsIntune()
    {
        var pc = AssetView.From(Win("OnlySccm"), All);
        Assert.Contains("nomdm", pc.Issues);
        Assert.Equal(States.Risk, pc.State);
        Assert.Equal(80, pc.Score);

        var mac = new Asset { Platform = "macOS", Ownership = "Corporate", IsActive = true, InIntune = true, IntuneChannel = "Mdm", Coverage = "OnlyIntune" };
        Assert.True(AssetView.From(mac, All).MeetsExpected);
        Assert.Empty(AssetView.From(mac, All).Issues);
    }

    [Fact]
    public void RulesAreEvaluatedOnlyWhenTheirSourceIsAvailable()
    {
        var noIntune = new SourceAvailability(true, false, false, true);
        Assert.DoesNotContain("nomdm", AssetView.From(Win("OnlySccm"), noIntune).Issues);
    }

    [Fact]
    public void DiscoveredWithoutClientIsAHighPriorityIssueOnlyWhenActive()
    {
        var a = Win("Neither");
        a.InSccm = true;
        a.SccmHealth = "NoClient";
        Assert.Contains("noclient", AssetView.From(a, All).Issues);
        a.IsActive = false;
        var stale = AssetView.From(a, All);
        Assert.DoesNotContain("noclient", stale.Issues);
        Assert.Equal(States.Stale, stale.State);
    }

    [Fact]
    public void WeightsLowerTheScoreAndStaleDoesNotCount()
    {
        Assert.Equal(60, HealthModel.Score(["nomdm", "noclient"]));
        Assert.Equal(100, HealthModel.Score(["stale"]));
        Assert.Equal(0, HealthModel.Score(["rooted", "byodnoprot", "noclient"]));
    }

    [Fact]
    public void StateFollowsTheWorstPriority()
    {
        var a = Win();
        Assert.Equal(States.Attention, HealthModel.StateOf(a, ["noncomp"]));
        Assert.Equal(States.Risk, HealthModel.StateOf(a, ["noncomp", "eol"]));
        Assert.Equal(States.Healthy, HealthModel.StateOf(a, []));
    }

    [Fact]
    public void RulesThatNeedUncollectedDataAreListedAsUnavailable()
    {
        var minimal = new SourceAvailability(true, true, true, true);
        Assert.All(new[] { "byodnoprot", "nopolicy", "cfgfail", "userdis" }, id => Assert.False(HealthModel.RuleOf(id).IsAvailable(minimal)));
        Assert.All(new[] { "patch", "cleval", "oslow" }, id => Assert.False(HealthModel.RuleOf(id).IsAvailable(SourceAvailability.All)));
        Assert.All(new[] { "noclient", "nomdm", "eol", "nomgr", "noncomp", "stalecomm", "review", "stale", "rooted", "nobitlocker" },
            id => Assert.True(HealthModel.RuleOf(id).IsAvailable(minimal)));
        Assert.All(new[] { "byodnoprot", "nopolicy", "cfgfail", "userdis" }, id => Assert.True(HealthModel.RuleOf(id).IsAvailable(SourceAvailability.All)));
        Assert.Contains("MAM", HealthModel.RuleOf("byodnoprot").WhyUnavailable(minimal));
    }

    [Fact]
    public void HeadlineDependsOnTargetsAndRiskNotOnlyOnTheAverage()
    {
        var assets = Enumerable.Range(0, 10).Select(_ => Win()).ToList();
        var snapshot = new InventorySnapshot(assets.Select(a => AssetView.From(a, All)).ToList(), [], [], All, DateTimeOffset.UtcNow);
        var report = snapshot.Overview();
        Assert.Equal("Saudável", report.Headline);
        Assert.Equal(100, report.ParkScore);

        var risky = Enumerable.Range(0, 3).Select(_ => Win("OnlySccm")).Concat(Enumerable.Range(0, 7).Select(_ => Win())).ToList();
        var snap2 = new InventorySnapshot(risky.Select(a => AssetView.From(a, All)).ToList(), [], [], All, DateTimeOffset.UtcNow);
        Assert.Equal("Pede ação", snap2.Overview().Headline); // 30% in risk
    }

    [Fact]
    public void UnavailableKpisAreNotEnabledNeverZero()
    {
        var snapshot = new InventorySnapshot([AssetView.From(Win(), All)], [], [], All, DateTimeOffset.UtcNow);
        var highlights = snapshot.Overview().Highlights;
        Assert.Equal(KpiState.NotEnabled, highlights.Single(k => k.Key == "patch").State);
        var withoutMam = new InventorySnapshot([AssetView.From(Win(), All)], [], [], new SourceAvailability(true, true, true, true), DateTimeOffset.UtcNow).Overview().Highlights;
        Assert.Equal(KpiState.NotEnabled, withoutMam.Single(k => k.Key == "byod").State);
        Assert.Null(withoutMam.Single(k => k.Key == "byod").Percent);
        Assert.Equal(KpiState.Available, highlights.Single(k => k.Key == "gestao").State);
    }

    [Fact]
    public void KpiCardReportsPointsMissingToTarget()
    {
        var k = new KpiCard("x", "x", KpiState.Available, 90, 100, 95, false, "");
        Assert.False(k.MeetsTarget);
        Assert.Equal(5, k.MissingPoints);
        Assert.True(new KpiCard("x", "x", KpiState.Available, 96, 100, 95, false, "").MeetsTarget);
    }
}

public class InventoryQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static AssetView View(Action<Nexus.Data.Entities.Asset> tweak)
    {
        var a = new Nexus.Data.Entities.Asset { Id = Guid.NewGuid(), Name = "PC-1", Platform = "WindowsClient", Ownership = "Corporate", IsActive = true, Model = "Latitude", PrimaryUser = "ana@x" };
        tweak(a);
        return AssetView.From(a, SourceAvailability.All);
    }

    [Fact]
    public void GroupByodCoversMobileAndComputers()
    {
        var mobile = View(a => { a.Platform = "iOS"; a.Ownership = "Personal"; a.InIntune = true; });
        var pcb = View(a => { a.Ownership = "Personal"; a.InIntune = true; });
        var corp = View(_ => { });
        var f = new InventoryFilter(Group: "byod");
        Assert.True(InventoryQuery.Matches(mobile, f, Now));
        Assert.True(InventoryQuery.Matches(pcb, f, Now));
        Assert.False(InventoryQuery.Matches(corp, f, Now));
    }

    [Fact]
    public void FunnelFiltersReturnExactlyWhoDroppedOut()
    {
        var noClient = View(a => { a.InSccm = true; a.SccmClient = false; a.SccmHealth = "NoClient"; });
        var silent = View(a => { a.InSccm = true; a.SccmClient = true; a.SccmLastSeenAt = Now.AddDays(-45); });
        var fine = View(a => { a.InSccm = true; a.SccmClient = true; a.SccmLastSeenAt = Now.AddDays(-1); });
        Assert.True(InventoryQuery.Funnel(noClient, "sccm-sem-cliente", Now));
        Assert.False(InventoryQuery.Funnel(fine, "sccm-sem-cliente", Now));
        Assert.True(InventoryQuery.Funnel(silent, "sccm-silencioso", Now));
        Assert.False(InventoryQuery.Funnel(fine, "sccm-silencioso", Now));
    }

    [Fact]
    public void SearchMatchesNameUserSerialAndModelCaseInsensitively()
    {
        var v = View(a => a.Serial = "ABC123");
        Assert.True(InventoryQuery.Matches(v, new InventoryFilter(Query: "abc1"), Now));
        Assert.True(InventoryQuery.Matches(v, new InventoryFilter(Query: "LATITUDE"), Now));
        Assert.True(InventoryQuery.Matches(v, new InventoryFilter(Query: "ana@"), Now));
        Assert.False(InventoryQuery.Matches(v, new InventoryFilter(Query: "zzz"), Now));
    }

    [Fact]
    public void DefaultSortPutsTheWorstDevicesFirst()
    {
        var bad = View(a => { a.InSccm = true; a.SccmHealth = "NoClient"; a.Name = "B"; });
        var good = View(a => { a.Name = "A"; a.InIntune = true; a.IntuneChannel = "Mdm"; a.InSccm = true; a.SccmClient = true; a.SccmHealth = "Healthy"; a.Coverage = "Both"; });
        var sorted = InventoryQuery.Sort([good, bad], null, false).ToList();
        Assert.Equal("B", sorted[0].Asset.Name);
    }
}

public class AccessPolicyTests
{
    private static Nexus.Web.Setup.AccessDecision Decide(string path, bool role, bool open, bool setup) =>
        Nexus.Web.Setup.AccessPolicy.Decide(path, role, open, setup);

    [Theory]
    [InlineData("/painel")]
    [InlineData("/inventario")]
    [InlineData("/dispositivo/abc")]
    [InlineData("/_blazor")]
    public void InventoryScreensAreOpenToAnonymousVisitorsByDefault(string path) =>
        Assert.Equal(Nexus.Web.Setup.AccessDecision.Allow, Decide(path, role: false, open: true, setup: true));

    [Theory]
    [InlineData("/assistente")]
    [InlineData("/saude")]
    [InlineData("/diagnostico")]
    public void OperatorScreensNeedTheSetupRoleEvenWhenInventoryIsOpen(string path)
    {
        Assert.Equal(Nexus.Web.Setup.AccessDecision.RequireSetupAccess, Decide(path, role: false, open: true, setup: true));
        Assert.Equal(Nexus.Web.Setup.AccessDecision.Allow, Decide(path, role: true, open: true, setup: true));
    }

    [Fact]
    public void ClosingOpenAccessSendsEveryoneToTheSetupCode()
    {
        Assert.Equal(Nexus.Web.Setup.AccessDecision.RequireSetupAccess, Decide("/painel", role: false, open: false, setup: true));
        Assert.Equal(Nexus.Web.Setup.AccessDecision.Closed, Decide("/painel", role: false, open: false, setup: false));
        Assert.Equal(Nexus.Web.Setup.AccessDecision.Allow, Decide("/painel", role: true, open: false, setup: true));
    }

    [Fact]
    public void AfterSetupModeEndsOperatorScreensStayClosedButInventoryStaysOpen()
    {
        Assert.Equal(Nexus.Web.Setup.AccessDecision.Closed, Decide("/saude", role: false, open: true, setup: false));
        Assert.Equal(Nexus.Web.Setup.AccessDecision.Allow, Decide("/painel", role: false, open: true, setup: false));
    }
}
