using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nexus.Collectors.Xdr;
using Nexus.Core;
using Nexus.Core.Configuration;
using Nexus.Core.Health;
using Nexus.Data.Entities;
using Nexus.Data.Support;
using Nexus.Reconciliation;
using Nexus.Simulation;
using Nexus.Worker.Collection;

namespace Nexus.Tests;

public class XdrReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static SccmDeviceRecord Sccm(int id, string name, int daysAgo = 1) => new()
    {
        ResourceId = id, Name = name, Client = true, Active = true, Obsolete = false, OperatingSystem = "Microsoft Windows 11 Enterprise", LastActiveAt = Now.AddDays(-daysAgo), ClientActiveStatus = 1,
    };

    private static XdrEndpointRecord Xdr(string id, string host, string status = "CONNECTED", int daysAgo = 0, string type = "AGENT_TYPE_WORKSTATION") => new()
    {
        AgentId = id, HostName = host, AgentStatus = status, OperationalStatus = "PROTECTED", AgentType = type, Ip = "10.0.0.1", LastSeenAt = Now.AddDays(-daysAgo),
    };

    private static AdComputerRecord Ad(string name) => new() { ObjectGuid = Guid.NewGuid(), Name = name, OperatingSystem = "Windows 11", LastLogonTimestamp = Now.AddDays(-2), Enabled = true };

    private static ReconcileResult Run(IEnumerable<SccmDeviceRecord>? sccm = null, IEnumerable<XdrEndpointRecord>? xdr = null, IEnumerable<AdComputerRecord>? ad = null) =>
        Reconciler.Run(new ReconcileInput((sccm ?? []).ToList(), (ad ?? []).ToList(), [], [], [], Now, TimeSpan.FromDays(30), Xdr: (xdr ?? []).ToList()));

    [Fact]
    public void XdrAttachesByShortNameEvenWhenTheTableHoldsTheFqdn()
    {
        var result = Run([Sccm(1, "PC-01")], [Xdr("a1", "pc-01.corp.azul.sim")]);
        var asset = Assert.Single(result.Assets);
        Assert.True(asset is { InXdr: true, XdrByNameOnly: true });
        Assert.Equal("High", asset.Confidence); // supporting evidence does not weaken the management join
        Assert.Contains(result.Links, l => l is { Source: "xdr", Confidence: "Low", Evidence: "name" });
    }

    [Fact]
    public void XdrLastSeenCountsAsActivity()
    {
        var result = Run([Sccm(1, "PC-01", daysAgo: 90)], [Xdr("a1", "PC-01", daysAgo: 1)]);
        var asset = Assert.Single(result.Assets);
        Assert.True(asset.IsActive);                       // the agent reported yesterday, SCCM did not
        Assert.Equal(Now.AddDays(-1), asset.LastActivityAt);
    }

    [Fact]
    public void XdrOnlyHostBecomesACorporateAssetThatNobodyManages()
    {
        var result = Run(xdr: [Xdr("a1", "NOVO-HOST")]);
        var asset = Assert.Single(result.Assets);
        Assert.Equal(("NOVO-HOST", "Corporate", "xdr", "Neither"), (asset.Name, asset.Ownership, asset.OwnershipSource, asset.Coverage));
        var view = AssetView.From(asset, SourceAvailability.All);
        Assert.Contains("nomgr", view.Issues);
    }

    [Fact]
    public void AdAndXdrWithTheSameNameAndNoManagementRecordAreOneAsset()
    {
        var asset = Assert.Single(Run(xdr: [Xdr("a1", "old-pc.corp.azul.sim")], ad: [Ad("OLD-PC")]).Assets);
        Assert.True(asset is { InAd: true, InXdr: true });
    }

    [Fact]
    public void TwoAgentsOfOneHostAttachToTheSingleAssetAndRaiseAnInformationalReview()
    {
        var result = Run([Sccm(1, "PC-01")], [Xdr("a1", "PC-01", "LOST", 120), Xdr("a2", "PC-01")]);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("CONNECTED", asset.XdrStatus);        // the live agent is the one shown
        Assert.Contains(result.Review, r => r.Kind == "MultipleXdrRecords");
    }

    [Fact]
    public void AmbiguousManagementNamesNeverAttachTheAgent()
    {
        var result = Run([Sccm(1, "PC-01"), Sccm(2, "pc-01")], [Xdr("a1", "PC-01")]);
        Assert.Equal(3, result.Assets.Count);
        Assert.Contains(result.Review, r => r.Kind == "AmbiguousName");
        Assert.DoesNotContain(result.Assets, a => a.InXdr && a.InSccm);
    }

    [Fact]
    public void MissingAgentIsAnIssueOnlyWhenTheXdrSourceIsAvailable()
    {
        var noAgent = Run([Sccm(1, "PC-01")]).Assets.Single();
        Assert.Contains("noxdr", AssetView.From(noAgent, SourceAvailability.All).Issues);
        Assert.DoesNotContain("noxdr", AssetView.From(noAgent, new SourceAvailability(true, true, true, true)).Issues);
    }

    [Theory]
    [InlineData("CONNECTED", 0, 0, false)]
    [InlineData("DISCONNECTED", 20, 0, true)]
    [InlineData("LOST", 90, 0, true)]
    [InlineData("CONNECTED", 12, 0, true)]   // other tools saw the device 12 days after the agent
    [InlineData("CONNECTED", 3, 0, false)]
    public void DisconnectedOrSilentAgentsAreFlagged(string status, int xdrDaysAgo, int otherDaysAgo, bool flagged)
    {
        var asset = Run([Sccm(1, "PC-01", otherDaysAgo)], [Xdr("a1", "PC-01", status, xdrDaysAgo)]).Assets.Single();
        Assert.Equal(flagged, AssetView.From(asset, SourceAvailability.All).Issues.Contains("xdroff"));
    }

    [Fact]
    public void EdrCoverageCountsOnlyConnectedAgentsOfActiveWindowsMachines()
    {
        var result = Run([Sccm(1, "A"), Sccm(2, "B"), Sccm(3, "C")], [Xdr("1", "A"), Xdr("2", "B", "DISCONNECTED", 20)]);
        var snapshot = new InventorySnapshot(result.Assets.Select(a => AssetView.From(a, SourceAvailability.All)).ToList(), [], [], SourceAvailability.All, DateTimeOffset.UtcNow);
        var edr = snapshot.Overview().Highlights.Single(k => k.Key == "edr");
        Assert.Equal((1, 3), (edr.Numerator, edr.Denominator));
        var off = new InventorySnapshot(snapshot.Views, [], [], new SourceAvailability(true, true, true, true), DateTimeOffset.UtcNow).Overview().Highlights.Single(k => k.Key == "edr");
        Assert.Equal(KpiState.NotEnabled, off.State);
    }
}

public class XdrReaderTests
{
    [Theory]
    [InlineData("API_Cortex_getAllEndpoints", "[dbo].[API_Cortex_getAllEndpoints]")]
    [InlineData("cortex.endpoints", "[cortex].[endpoints]")]
    public void TableNamesAreQuotedAndValidated(string table, string expected) => Assert.Equal(expected, XdrIdentifiers.QuotedTable(table));

    [Theory]
    [InlineData("endpoints; DROP TABLE x")]
    [InlineData("a.b.c")]
    [InlineData("tab le")]
    [InlineData("]; --")]
    [InlineData("")]
    public void SuspiciousTableNamesAreRejected(string table) => Assert.Throws<ArgumentException>(() => XdrIdentifiers.QuotedTable(table));

    [Fact]
    public void DatabaseNameIsValidated() => Assert.Throws<ArgumentException>(() => XdrIdentifiers.Database("cortex_db; --"));

    [Fact]
    public async Task AccessCheckExplainsAMissingServer()
    {
        var result = await new XdrAccessCheck(new XdrSettings(), @"CORP\svc", new FakeTimeProvider()).CheckAsync(default);
        Assert.Equal(HealthStatus.NotConfigured, result.Status);
    }

    [Fact]
    public void GrantScriptMatchesTheAccountBySidAndCreatesOneRole()
    {
        var script = XdrGrantScript.Grant("cortex_db", "API_Cortex_getAllEndpoints", @"AZUL\svc.sccm");
        Assert.Contains("SUSER_SID", script);
        Assert.Contains("GRANT SELECT ON [dbo].[API_Cortex_getAllEndpoints] TO [azul_nexus_xdr_reader]", script);
        Assert.DoesNotContain("db_owner", script);
    }
}

public class XdrPipelineTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
    private readonly SyntheticEstate _estate = new();
    private readonly FakeTimeProvider _clock = new(SyntheticEstate.Now.AddMinutes(30));
    private readonly SwitchableSources _sources;
    private readonly JobRunner _runner;

    public XdrPipelineTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var paths = new NexusPaths(_dataDir);
        var settings = new NexusSettings();
        settings.Sccm.Mode = SourceMode.Simulated;
        settings.ActiveDirectory.Mode = SourceMode.Simulated;
        new SettingsStore(paths).Save(settings);
        _sources = new SwitchableSources(_estate);
        _runner = new JobRunner(_db, new SettingsProvider(paths), _sources, new CollectionGate(new FixedLoad(10), _clock), _clock, NullLogger<JobRunner>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true);
    }

    private async Task CollectAllAsync()
    {
        foreach (var job in JobNames.Collections)
        {
            Assert.Equal(JobRunner.StatusSucceeded, (await _runner.RunAsync(job, default)).Status);
        }
    }

    [Fact]
    public async Task XdrTableIsCollectedAndJoinedToTheAssets()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        Assert.Equal(_estate.XdrEndpoints.Count, await db.XdrEndpoints.CountAsync());
        var assets = await db.Assets.ToListAsync();
        Assert.Contains(assets, a => a is { InXdr: true, InSccm: true, XdrByNameOnly: true });
        Assert.Contains(assets, a => a.InXdr && a.Name.StartsWith("AZ-XDR-ONLY", StringComparison.Ordinal) && !a.InSccm && !a.InIntune && !a.InAd);

        var snapshot = await InventorySnapshotLoader.LoadAsync(_db, _clock, default);
        Assert.True(snapshot.Sources.Xdr);
        Assert.Contains(snapshot.Views, v => v.Issues.Contains("noxdr"));
        Assert.Contains(snapshot.Views, v => v.Issues.Contains("xdroff"));
        var edr = snapshot.Overview().Highlights.Single(k => k.Key == "edr");
        Assert.Equal(KpiState.Available, edr.State);
        Assert.InRange(edr.Percent ?? 0, 50, 100);
    }

    [Fact]
    public async Task XdrFailureKeepsTheLastSnapshotAndUnconfiguredIsNotZero()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        var before = await db.XdrEndpoints.CountAsync();
        _sources.FailXdr = true;
        Assert.Equal(JobRunner.StatusFailed, (await _runner.RunAsync(JobNames.Xdr, default)).Status);
        Assert.Equal(before, await db.XdrEndpoints.CountAsync());

        _sources.XdrEnabled = false;
        Assert.Equal(JobRunner.StatusNotConfigured, (await _runner.RunAsync(JobNames.Xdr, default)).Status);
    }
}
