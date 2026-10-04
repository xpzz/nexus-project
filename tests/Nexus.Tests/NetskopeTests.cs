using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nexus.Collectors.Netskope;
using Nexus.Core;
using Nexus.Core.Configuration;
using Nexus.Core.Health;
using Nexus.Core.Security;
using Nexus.Data.Entities;
using Nexus.Data.Support;
using Nexus.Reconciliation;
using Nexus.Simulation;
using Nexus.Worker.Collection;

namespace Nexus.Tests;

public class NetskopeParserTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void V1ClientWithNestedHostInfoIsMapped()
    {
        var c = NetskopeParser.Parse(Json("""
            {"_id":"abc123","client_install_time":1727740800,"client_version":"126.0.1.2","device_id":"dev-9",
             "host_info":{"hostname":"PC-01","os":"Windows","os_version":"10.0.22631","serial_number":"SN123","device_make":"Dell Inc.","device_model":"Latitude","managementID":"11111111-1111-1111-1111-111111111111"},
             "last_event":{"actor":"Netskope Client","event":"Heartbeat","status":"Enabled","timestamp":1759320000},
             "users":[{"username":"ana@azul.example"},{"username":"bruno@azul.example"}]}
            """))!;
        Assert.Equal(("abc123", "PC-01", "Windows", "SN123", "Dell Inc.", "Enabled", "126.0.1.2"), (c.Id, c.HostName, c.OperatingSystem, c.Serial, c.Manufacturer, c.Status, c.ClientVersion));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1759320000), c.LastEventAt);
        Assert.Equal("11111111-1111-1111-1111-111111111111", c.ManagementId);
        Assert.Equal("ana@azul.example; bruno@azul.example", c.Users);
    }

    [Fact]
    public void FlatClientAndMillisecondTimestampsAreAccepted()
    {
        var c = NetskopeParser.Parse(Json("""{"id":"x1","hostname":"pc-02.corp.azul.sim","os":"macOS","last_seen":1759320000000,"agent_status":"Disabled","user":"carla@azul.example"}"""))!;
        Assert.Equal(("x1", "pc-02.corp.azul.sim", "Disabled", "carla@azul.example"), (c.Id, c.HostName, c.Status, c.Users));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1759320000000), c.LastEventAt);
    }

    [Theory]
    [InlineData("""{"hostname":"sem-id"}""")]
    [InlineData("""[1,2]""")]
    public void RecordsWithoutAnIdentifierAreSkipped(string json) => Assert.Null(NetskopeParser.Parse(Json(json)));

    [Theory]
    [InlineData("""{"status":"success","data":[{"_id":"1"},{"_id":"2"}],"total":2}""", 2)]
    [InlineData("""{"result":[{"_id":"1"}]}""", 1)]
    [InlineData("""[{"_id":"1"},{"_id":"2"},{"_id":"3"}]""", 3)]
    [InlineData("""{"data":{"devices":[{"_id":"1"}]}}""", 1)]
    [InlineData("""{"status":"success"}""", 0)]
    public void RecordsAreFoundInEveryKnownEnvelope(string json, int expected) => Assert.Equal(expected, NetskopeParser.Records(Json(json)).Count());

    [Fact]
    public void ZeroTimestampMeansNever() => Assert.Null(NetskopeParser.Parse(Json("""{"_id":"z","last_event":{"timestamp":0}}"""))!.LastEventAt);
}

public class NetskopeReaderTests
{
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static NetskopeSettings Settings(int pageSize = 2, string placement = "query") => new()
    {
        Tenant = "azul.goskope.com", ClientsPath = "/api/v1/clients", TokenPlacement = placement, PageSize = pageSize, OffsetParameter = "skip", Mode = SourceMode.Live,
    };

    private static string Page(params string[] ids) => "{\"status\":\"success\",\"data\":[" + string.Join(",", ids.Select(i => $"{{\"_id\":\"{i}\",\"host_info\":{{\"hostname\":\"H{i}\"}}}}")) + "]}";

    [Fact]
    public async Task PagesByLimitAndSkipUntilAShortPage()
    {
        var handler = new StubHandler(r => Json(r.RequestUri!.Query.Contains("skip=0") ? Page("a", "b") : r.RequestUri.Query.Contains("skip=2") ? Page("c", "d") : Page("e")));
        var reader = new NetskopeHttpReader(new HttpClient(handler), Settings(), "s3cret");
        var ids = new List<string>();
        await foreach (var c in reader.ReadClientsAsync(default)) ids.Add(c.Id);
        Assert.Equal(["a", "b", "c", "d", "e"], ids);
        Assert.Equal(3, handler.Urls.Count);
        Assert.All(handler.Urls, u => Assert.Contains("token=s3cret", u));
    }

    [Fact]
    public async Task AnApiThatIgnoresTheOffsetDoesNotLoopForever()
    {
        var handler = new StubHandler(_ => Json(Page("a", "b")));
        var reader = new NetskopeHttpReader(new HttpClient(handler), Settings(), "t");
        var ids = new List<string>();
        await foreach (var c in reader.ReadClientsAsync(default)) ids.Add(c.Id);
        Assert.Equal(["a", "b"], ids);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task V2TokenGoesInTheHeaderNotInTheUrl()
    {
        string? header = null;
        var handler = new StubHandler(r => { header = r.Headers.TryGetValues("Netskope-Api-Token", out var v) ? v.Single() : null; return Json(Page("a")); });
        var reader = new NetskopeHttpReader(new HttpClient(handler), Settings(placement: "header"), "tok-v2");
        await foreach (var _ in reader.ReadClientsAsync(default)) { }
        Assert.Equal("tok-v2", header);
        Assert.DoesNotContain("tok-v2", handler.Urls.Single());
    }

    [Fact]
    public void TheDisplayedUrlNeverHoldsTheToken() => Assert.DoesNotContain("token=", NetskopeHttpReader.BuildUrl(Settings(), 0, withToken: false));

    [Fact]
    public async Task UnauthorizedIsTranslatedAndTheTokenNeverLeaks()
    {
        var handler = new StubHandler(_ => Json("""{"message":"invalid token"}""", HttpStatusCode.Unauthorized));
        var reader = new NetskopeHttpReader(new HttpClient(handler), Settings(), "my-secret-token");
        var ex = await Assert.ThrowsAsync<NetskopeException>(async () => { await foreach (var _ in reader.ReadClientsAsync(default)) { } });
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.False(string.IsNullOrWhiteSpace(ex.Error.Impact));
        Assert.False(string.IsNullOrWhiteSpace(ex.Error.HowToFix));
        Assert.DoesNotContain("my-secret-token", ex.Message);
    }

    [Fact]
    public async Task ThrottlingHonorsRetryAfter()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            if (++calls == 1)
            {
                var r = Json("{}", HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
                return r;
            }

            return Json(Page("a"));
        });
        var delays = new List<TimeSpan>();
        var reader = new NetskopeHttpReader(new HttpClient(handler), Settings(), "t", (d, _) => { delays.Add(d); return Task.CompletedTask; });
        var ids = new List<string>();
        await foreach (var c in reader.ReadClientsAsync(default)) ids.Add(c.Id);
        Assert.Equal(["a"], ids);
        Assert.Equal([TimeSpan.FromSeconds(9)], delays);
    }

    [Fact]
    public async Task V1ErrorsReportedWithHttp200AreRaised()
    {
        var handler = new StubHandler(_ => Json("""{"status":"error","errorCode":"Bad request"}"""));
        var reader = new NetskopeHttpReader(new HttpClient(handler), Settings(), "t");
        var ex = await Assert.ThrowsAsync<NetskopeException>(async () => { await foreach (var _ in reader.ReadClientsAsync(default)) { } });
        Assert.Contains("Bad request", ex.Error.WhatHappened);
    }

    [Fact]
    public async Task AccessCheckReportsCountAndTranslatesFailures()
    {
        var ok = await new NetskopeAccessCheck(Settings(), () => new FakeNetskopeReader([new NetskopeClient("1", null, "H", null, null, null, null, null, null, null, DateTimeOffset.UtcNow.AddHours(-3), null, null, null)]), TimeProvider.System).CheckAsync(default);
        Assert.Equal(HealthStatus.Ok, ok.Status);
        var denied = await new NetskopeAccessCheck(Settings(), () => new FakeNetskopeReader([]) { FailWithUnauthorized = true }, TimeProvider.System).CheckAsync(default);
        Assert.Equal(HealthStatus.Error, denied.Status);
        Assert.NotNull(denied.Error);
        var none = await new NetskopeAccessCheck(new NetskopeSettings(), () => null, TimeProvider.System).CheckAsync(default);
        Assert.Equal(HealthStatus.NotConfigured, none.Status);
        var noToken = await new NetskopeAccessCheck(Settings(), () => null, TimeProvider.System).CheckAsync(default);
        Assert.Equal(HealthStatus.NotConfigured, noToken.Status);
    }
}

public class NetskopeSecretsTests
{
    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string secret) => new(secret.Reverse().ToArray());
        public string Unprotect(string protectedSecret) => new(protectedSecret.Reverse().ToArray());
    }

    [Fact]
    public void ProtectedTokenIsResolvedAndNeverExported()
    {
        var settings = new NexusSettings();
        settings.Netskope.ProtectedToken = new ReversingProtector().Protect("tok");
        Assert.Equal("tok", NetskopeToken.Resolve(settings.Netskope, new ReversingProtector()));
        Assert.DoesNotContain(settings.Netskope.ProtectedToken, SettingsStore.Export(settings));
        var imported = SettingsStore.Import(SettingsStore.Export(settings), settings);
        Assert.Equal(settings.Netskope.ProtectedToken, imported.Netskope.ProtectedToken); // the server keeps its own secret
    }

    [Fact]
    public void WithoutAnyTokenTheResolverReturnsNull() => Assert.Null(NetskopeToken.Resolve(new NetskopeSettings()));
}

public class NetskopeReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AadA = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SccmDeviceRecord Sccm(int id, string name, Guid? aad = null, string? serial = null, int daysAgo = 1) => new()
    {
        ResourceId = id, Name = name, Client = true, Active = true, Obsolete = false, AadDeviceId = aad, Serial = serial, Manufacturer = "Dell Inc.",
        OperatingSystem = "Microsoft Windows 11 Enterprise", LastActiveAt = Now.AddDays(-daysAgo), ClientActiveStatus = 1,
    };

    private static NetskopeClientRecord Ns(string id, string host, string? managementId = null, string? serial = null, int daysAgo = 0) => new()
    {
        Id = id, HostName = host, ManagementId = managementId, Serial = serial, Manufacturer = serial is null ? null : "Dell Inc.", Status = "Enabled", LastEventAt = Now.AddDays(-daysAgo), OperatingSystem = "Windows 11",
    };

    private static ReconcileResult Run(IEnumerable<SccmDeviceRecord>? sccm = null, IEnumerable<NetskopeClientRecord>? ns = null, IEnumerable<EntraDeviceRecord>? entra = null, IEnumerable<IntuneDeviceRecord>? intune = null) =>
        Reconciler.Run(new ReconcileInput((sccm ?? []).ToList(), [], (intune ?? []).ToList(), (entra ?? []).ToList(), [], Now, TimeSpan.FromDays(30), Netskope: (ns ?? []).ToList()));

    [Fact]
    public void ManagementIdThatIsTheEntraDeviceIdJoinsWithHighConfidence()
    {
        var result = Run([Sccm(1, "PC-ONE", AadA)], [Ns("n1", "RENAMED-PC", AadA.ToString())]);
        var asset = Assert.Single(result.Assets);
        Assert.True(asset is { InNetskope: true, InSccm: true, NetskopeByNameOnly: false });
        Assert.Equal("High", asset.Confidence);
        Assert.Contains(result.Links, l => l is { Source: "netskope", Evidence: "management-id", Confidence: "High" });
    }

    [Fact]
    public void ManagementIdThatIsTheIntuneDeviceIdJoinsToo()
    {
        var intuneId = Guid.NewGuid();
        var result = Run(ns: [Ns("n1", "X", intuneId.ToString())], intune: [new IntuneDeviceRecord { Id = intuneId.ToString(), DeviceName = "Y", ManagementAgent = "mdm", OperatingSystem = "Windows", LastSyncAt = Now }]);
        var asset = Assert.Single(result.Assets);
        Assert.True(asset is { InNetskope: true, InIntune: true });
    }

    [Fact]
    public void ValidSerialJoinsWithMediumConfidence()
    {
        var result = Run([Sccm(1, "PC-ONE", serial: "ABC12345")], [Ns("n1", "OTHER-NAME", serial: "abc12345")]);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("Medium", asset.Confidence);
        Assert.Contains(result.Links, l => l is { Source: "netskope", Evidence: "serial" });
    }

    [Fact]
    public void NameAloneAttachesAsLowConfidenceSupportWithoutWeakeningTheJoin()
    {
        var result = Run([Sccm(1, "PC-ONE")], [Ns("n1", "pc-one.corp.azul.sim")]);
        var asset = Assert.Single(result.Assets);
        Assert.True(asset is { InNetskope: true, NetskopeByNameOnly: true });
        Assert.Equal("High", asset.Confidence);
    }

    [Fact]
    public void NetskopeOnlyHostIsACorporateAssetNobodyManages()
    {
        var asset = Assert.Single(Run(ns: [Ns("n1", "SOLO")]).Assets);
        Assert.Equal(("SOLO", "Corporate", "netskope", "Neither", "WindowsClient"), (asset.Name, asset.Ownership, asset.OwnershipSource, asset.Coverage, asset.Platform));
        Assert.Contains("nomgr", AssetView.From(asset, SourceAvailability.All).Issues);
    }

    [Fact]
    public void XdrAndNetskopeWithTheSameNameAndNothingElseAreOneAsset()
    {
        var result = Reconciler.Run(new ReconcileInput([], [], [], [], [], Now, TimeSpan.FromDays(30),
            Xdr: [new XdrEndpointRecord { AgentId = "x", HostName = "HOST-1", AgentStatus = "CONNECTED", LastSeenAt = Now }], Netskope: [Ns("n1", "host-1.corp")]));
        var asset = Assert.Single(result.Assets);
        Assert.True(asset is { InXdr: true, InNetskope: true });
    }

    [Fact]
    public void TwoClientsOfOneHostAttachToOneAssetWithAnInformationalReview()
    {
        var result = Run([Sccm(1, "PC-ONE")], [Ns("n1", "PC-ONE", daysAgo: 100), Ns("n2", "PC-ONE")]);
        Assert.Single(result.Assets);
        Assert.Contains(result.Review, r => r.Kind == "MultipleNetskopeRecords");
    }

    [Fact]
    public void NetskopeLastEventIsAnActivitySignal()
    {
        var asset = Assert.Single(Run([Sccm(1, "PC-ONE", daysAgo: 90)], [Ns("n1", "PC-ONE", daysAgo: 0)]).Assets);
        Assert.True(asset.IsActive);
        Assert.Equal("Single", asset.ActivityClass);
        Assert.Equal("netskope", asset.ActiveSources);
    }

    [Fact]
    public void MissingClientIsAnIssueOnlyWhenTheSourceIsAvailable()
    {
        var asset = Run([Sccm(1, "PC-ONE")]).Assets.Single();
        Assert.Contains("nonetskope", AssetView.From(asset, SourceAvailability.All).Issues);
        Assert.DoesNotContain("nonetskope", AssetView.From(asset, new SourceAvailability(true, true, true, true, Xdr: true)).Issues);
    }
}

public class ActivityModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private static Asset Seen(Action<Asset> set)
    {
        var a = new Asset { Name = "X", Platform = "WindowsClient", Ownership = "Corporate" };
        set(a);
        ActivityModel.Apply(a, Now, Window);
        return a;
    }

    [Theory]
    [InlineData(0, 0, "Inactive")]
    [InlineData(1, 1, "Single")]
    [InlineData(1, 0, "Unconfirmed")]
    [InlineData(2, 1, "Confirmed")]
    [InlineData(2, 0, "Confirmed")]
    [InlineData(5, 4, "Confirmed")]
    public void ClassFollowsTheNumberAndStrengthOfTheReports(int reported, int strong, string expected) => Assert.Equal(expected, ActivityModel.ClassOf(reported, strong));

    [Fact]
    public void AStrongSourceAloneIsInThePool()
    {
        var a = Seen(x => x.XdrLastSeenAt = Now.AddDays(-2));
        Assert.True(a.IsActive);
        Assert.Equal(("Single", 1, "xdr"), (a.ActivityClass, a.ActiveSourceCount, a.ActiveSources));
    }

    [Fact]
    public void AdLogonAloneDoesNotProveUse()
    {
        var a = Seen(x => x.AdLastLogonAt = Now.AddDays(-2));
        Assert.False(a.IsActive);
        Assert.Equal("Unconfirmed", a.ActivityClass);
        var view = AssetView.From(a, SourceAvailability.All);
        Assert.Equal(States.Unconfirmed, view.State);
        Assert.DoesNotContain("stale", view.Issues);
    }

    [Fact]
    public void AdPlusEntraTogetherAreConfirmed()
    {
        var a = Seen(x => { x.AdLastLogonAt = Now.AddDays(-3); x.EntraLastSignInAt = Now.AddDays(-1); });
        Assert.True(a.IsActive);
        Assert.Equal("Confirmed", a.ActivityClass);
    }

    [Fact]
    public void ReportsOutsideTheWindowDoNotCount()
    {
        var a = Seen(x => { x.SccmLastSeenAt = Now.AddDays(-45); x.XdrLastSeenAt = Now.AddDays(-31); x.NetskopeLastSeenAt = Now.AddDays(-29); });
        Assert.Equal(("Single", "netskope"), (a.ActivityClass, a.ActiveSources));
        Assert.Equal(Now.AddDays(-29), a.LastActivityAt);
        Assert.Equal(Now.AddDays(-29), a.LastStrongActivityAt);
    }

    [Fact]
    public void NothingInTheWindowIsInactiveAndStale()
    {
        var a = Seen(x => x.SccmLastSeenAt = Now.AddDays(-120));
        Assert.False(a.IsActive);
        Assert.Equal("Inactive", a.ActivityClass);
        Assert.Contains("stale", AssetView.From(a, SourceAvailability.All).Issues);
        Assert.Null(a.ActiveSources);
    }

    [Fact]
    public void TheWindowIsConfigurable()
    {
        var a = new Asset { SccmLastSeenAt = Now.AddDays(-45) };
        ActivityModel.Apply(a, Now, TimeSpan.FromDays(60));
        Assert.True(a.IsActive);
        ActivityModel.Apply(a, Now, TimeSpan.FromDays(30));
        Assert.False(a.IsActive);
    }

    [Fact]
    public void SccmSilentWhileOtherAgentsAreActiveIsADivergence()
    {
        var a = Seen(x => { x.InSccm = true; x.SccmClient = true; x.SccmHealth = "Healthy"; x.SccmLastSeenAt = Now.AddDays(-28); x.NetskopeLastSeenAt = Now.AddHours(-3); x.InNetskope = true; x.InXdr = true; x.XdrStatus = "CONNECTED"; x.XdrLastSeenAt = Now; });
        Assert.Contains("stalecomm", AssetView.From(a, SourceAvailability.All).Issues);
    }

    [Fact]
    public void TheGapRuleStillSeesAnUnconfirmedAdOnlyMachine()
    {
        var a = Seen(x => { x.InAd = true; x.AdLastLogonAt = Now.AddDays(-1); x.Coverage = "Neither"; });
        Assert.False(a.IsActive);
        Assert.Contains("nomgr", AssetView.From(a, SourceAvailability.All).Issues);
    }
}

public class ActivePoolPipelineTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
    private readonly SyntheticEstate _estate = new();
    private readonly FakeTimeProvider _clock = new(SyntheticEstate.Now.AddMinutes(30));
    private readonly SwitchableSources _sources;
    private readonly JobRunner _runner;

    public ActivePoolPipelineTests()
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
    public async Task NetskopeIsCollectedJoinedAndCountedInTheActivity()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        Assert.Equal(_estate.NetskopeClients.Count, await db.NetskopeClients.CountAsync());
        var assets = await db.Assets.ToListAsync();
        Assert.Contains(assets, a => a is { InNetskope: true, InSccm: true, NetskopeByNameOnly: false });  // joined by management id or serial
        Assert.Contains(assets, a => a is { InNetskope: true, InSccm: true, NetskopeByNameOnly: true });
        Assert.Contains(assets, a => a.InNetskope && a.Name.StartsWith("AZ-NS-ONLY", StringComparison.Ordinal) && !a.InSccm);
        Assert.Contains(assets, a => a.ActiveSources != null && a.ActiveSources.Contains("netskope"));
    }

    [Fact]
    public async Task ThePoolSeparatesConfirmedSingleUnconfirmedAndInactive()
    {
        await CollectAllAsync();
        var snapshot = await InventorySnapshotLoader.LoadAsync(_db, _clock, default);
        var pool = snapshot.Overview().Pool;
        Assert.True(pool.Confirmed > 0 && pool.Single > 0 && pool.Unconfirmed > 0 && pool.Inactive > 0);
        Assert.Equal(pool.Confirmed + pool.Single, pool.Pool);
        Assert.Equal(snapshot.Views.Count, pool.Confirmed + pool.Single + pool.Unconfirmed + pool.Inactive);
        foreach (var source in pool.Sources)
        {
            Assert.Equal(pool.Pool, source.Reporting + source.Silent + source.Absent);
        }

        Assert.True(snapshot.Sources.Netskope);
        Assert.Contains(snapshot.Views, v => v.State == States.Unconfirmed);
        Assert.Contains(snapshot.Views, v => v.Issues.Contains("nonetskope"));
    }

    [Fact]
    public async Task NetskopeFailureKeepsTheSnapshotAndUnconfiguredIsNotZero()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        var before = await db.NetskopeClients.CountAsync();
        _sources.FailNetskope = true;
        Assert.Equal(JobRunner.StatusFailed, (await _runner.RunAsync(JobNames.Netskope, default)).Status);
        Assert.Equal(before, await db.NetskopeClients.CountAsync());
        _sources.NetskopeEnabled = false;
        Assert.Equal(JobRunner.StatusNotConfigured, (await _runner.RunAsync(JobNames.Netskope, default)).Status);
    }
}
