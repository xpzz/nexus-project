using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nexus.Collectors.Graph;
using Nexus.Core;
using Nexus.Core.Configuration;
using Nexus.Data.Entities;
using Nexus.Data.Support;
using Nexus.Reconciliation;
using Nexus.Simulation;
using Nexus.Worker.Collection;

namespace Nexus.Tests;

public class GraphDepthTests
{
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Resp(string id, int status, string? body = null, string? extra = null) =>
        "{\"id\":\"" + id + "\",\"status\":" + status + (body is null ? "" : ",\"body\":" + body) + (extra is null ? "" : "," + extra) + "}";

    private static GraphHttpClient Client(StubHandler handler, List<TimeSpan>? delays = null) =>
        new(new HttpClient(handler), new StaticToken(), (d, _) => { delays?.Add(d); return Task.CompletedTask; });

    [Fact]
    public async Task BatchSplitsInto20AndRetriesOnlyTheThrottledSubRequest()
    {
        var calls = 0;
        var handler = new StubHandler(r =>
        {
            calls++;
            var body = r.Content!.ReadAsStringAsync().Result;
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var items = doc.RootElement.GetProperty("requests").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();
            var responses = items.Select(id => id == "3" && calls == 1
                ? Resp(id, 429, null, "\"headers\":{\"Retry-After\":\"4\"}")
                : Resp(id, 200, "{\"value\":[]}"));
            return Json("{\"responses\":[" + string.Join(",", responses) + "]}");
        });
        var delays = new List<TimeSpan>();
        var result = await Client(handler, delays).BatchGetAsync(Enumerable.Range(0, 25).Select(i => (i.ToString(), $"/x/{i}")).ToList(), default);
        Assert.Equal(25, result.Count);
        Assert.All(result, r => Assert.Equal(200, r.Status));
        Assert.Equal(3, calls);                         // 20 + 5, then the single throttled one
        Assert.Contains(TimeSpan.FromSeconds(4), delays);
    }

    [Fact]
    public async Task ExtendedDeviceFieldsFallBackToBaseWhenTheTenantRejectsThem()
    {
        var handler = new StubHandler(r => r.RequestUri!.Query.Contains("isEncrypted")
            ? Json("""{"error":{"code":"BadRequest","message":"Could not find a property named 'isEncrypted'"}}""", HttpStatusCode.BadRequest)
            : Json("""{"value":[{"id":"m1","deviceName":"PC1","managementAgent":"mdm"}]}"""));
        var reader = new HttpGraphReader(Client(handler));
        var devices = new List<IntuneManagedDevice>();
        await foreach (var d in reader.ReadManagedDevicesAsync(default)) devices.Add(d);
        Assert.Single(devices);
        Assert.False(reader.UsedExtendedDeviceFields);
        Assert.Null(devices[0].IsEncrypted);
    }

    [Fact]
    public async Task ExtendedDeviceFieldsAreMapped()
    {
        var handler = new StubHandler(_ => Json("""{"value":[{"id":"m1","deviceName":"PC1","isEncrypted":true,"jailBroken":"False","totalStorageSpaceInBytes":1000,"freeStorageSpaceInBytes":400,"physicalMemoryInBytes":2048,"userId":"u1","autopilotEnrolled":true}]}"""));
        var reader = new HttpGraphReader(Client(handler));
        var d = await reader.ReadManagedDevicesAsync(default).FirstAsync2();
        Assert.True(reader.UsedExtendedDeviceFields);
        Assert.Equal((true, "False", 1000L, 400L, 2048L, "u1", true), (d.IsEncrypted, d.JailBroken, d.TotalStorageBytes, d.FreeStorageBytes, d.PhysicalMemoryBytes, d.UserId, d.AutopilotEnrolled));
    }

    [Fact]
    public async Task PoliciesCarryReadableAssignmentsWithGroupNames()
    {
        var handler = new StubHandler(r =>
        {
            var url = r.RequestUri!.ToString();
            if (r.Method == HttpMethod.Post)
            {
                var body = r.Content!.ReadAsStringAsync().Result;
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var ids = doc.RootElement.GetProperty("requests").EnumerateArray().Select(x => x.GetProperty("id").GetString()!);
                return Json("{\"responses\":[" + string.Join(",", ids.Select(id => Resp(id, 200, "{\"id\":\"" + id + "\",\"displayName\":\"Notebooks - Produção\"}"))) + "]}");
            }

            if (url.Contains("deviceCompliancePolicies"))
            {
                return Json("""
                    {"value":[{"@odata.type":"#microsoft.graph.windows10CompliancePolicy","id":"p1","displayName":"Windows - Conformidade","version":3,"lastModifiedDateTime":"2026-09-01T10:00:00Z",
                      "assignments":[{"target":{"@odata.type":"#microsoft.graph.groupAssignmentTarget","groupId":"11111111-1111-1111-1111-111111111111"}},{"target":{"@odata.type":"#microsoft.graph.exclusionGroupAssignmentTarget","groupId":"11111111-1111-1111-1111-111111111111"}},{"target":{"@odata.type":"#microsoft.graph.allDevicesAssignmentTarget"}}]}]}
                    """);
            }

            return Json("""{"value":[]}""");
        });
        var policies = new List<IntunePolicy>();
        await foreach (var p in new HttpGraphReader(Client(handler)).ReadPoliciesAsync(default)) policies.Add(p);
        var p1 = Assert.Single(policies);
        Assert.Equal(("compliance", "Windows", 3, true, 3), (p1.Kind, p1.Platform, p1.Version, p1.AssignedToAll, p1.AssignmentCount));
        Assert.Contains("Todos os dispositivos", p1.Assignments);
        Assert.Contains("Grupo: Notebooks - Produção", p1.Assignments);
        Assert.Contains("Exclui: Notebooks - Produção", p1.Assignments);
    }

    [Fact]
    public async Task OptionalPolicyKindsThatTheTenantDoesNotHaveAreSkipped()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.Contains("configurationPolicies") || r.RequestUri.AbsolutePath.Contains("ManagedAppProtections")
            ? Json("""{"error":{"code":"NotFound","message":"x"}}""", HttpStatusCode.NotFound)
            : Json("""{"value":[{"@odata.type":"#microsoft.graph.iosGeneralDeviceConfiguration","id":"c1","displayName":"iOS - Restrições"}]}"""));
        var policies = new List<IntunePolicy>();
        await foreach (var p in new HttpGraphReader(Client(handler)).ReadPoliciesAsync(default)) policies.Add(p);
        Assert.Contains(policies, p => p.Kind == "configuration" && p.Platform == "iOS");
    }

    [Fact]
    public async Task DevicePolicyStatesComeFromTheBatchAndMapToTheRightDevice()
    {
        var handler = new StubHandler(r =>
        {
            var body = r.Content!.ReadAsStringAsync().Result;
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var reqs = doc.RootElement.GetProperty("requests").EnumerateArray().Select(x => (Id: x.GetProperty("id").GetString()!, Url: x.GetProperty("url").GetString()!)).ToList();
            Assert.All(reqs, q => Assert.StartsWith("/deviceManagement/managedDevices/", q.Url));
            var responses = reqs.Select(q => q.Url.EndsWith("deviceCompliancePolicyStates")
                ? Resp(q.Id, 200, """{"value":[{"id":"s1","displayName":"Windows - Conformidade","state":"nonCompliant","platformType":"windows10AndLater","settingCount":12,"version":2}]}""")
                : Resp(q.Id, 200, """{"value":[{"id":"s2","displayName":"BitLocker","state":"error","settingCount":3}]}"""));
            return Json("{\"responses\":[" + string.Join(",", responses) + "]}");
        });
        var states = new List<DevicePolicyState>();
        await foreach (var s in new HttpGraphReader(Client(handler)).ReadDevicePolicyStatesAsync(["aaaaaaaa-0000-0000-0000-00000000000a", "bbbbbbbb-0000-0000-0000-00000000000b"], default)) states.Add(s);
        Assert.Equal(4, states.Count);
        Assert.Contains(states, s => s is { ManagedDeviceId: "bbbbbbbb-0000-0000-0000-00000000000b", Kind: "compliance", State: "nonCompliant", SettingCount: 12 });
        Assert.Contains(states, s => s is { ManagedDeviceId: "aaaaaaaa-0000-0000-0000-00000000000a", Kind: "configuration", PolicyName: "BitLocker", State: "error" });
    }

    [Fact]
    public async Task ForbiddenStatesExplainTheMissingPermission()
    {
        var handler = new StubHandler(r =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result);
            var ids = doc.RootElement.GetProperty("requests").EnumerateArray().Select(x => x.GetProperty("id").GetString()!);
            return Json("{\"responses\":[" + string.Join(",", ids.Select(id => Resp(id, 403, """{"error":{"code":"Forbidden"}}"""))) + "]}");
        });
        var ex = await Assert.ThrowsAsync<GraphException>(async () => { await foreach (var _ in new HttpGraphReader(Client(handler)).ReadDevicePolicyStatesAsync(["aaaaaaaa-0000-0000-0000-00000000000a"], default)) { } });
        Assert.Contains("DeviceManagementConfiguration", ex.Error.ToString());
    }

    [Fact]
    public async Task MamRegistrationsMapPlatformAppAndPolicyNames()
    {
        var handler = new StubHandler(_ => Json("""
            {"value":[{"@odata.type":"#microsoft.graph.iosManagedAppRegistration","id":"r1","userId":"u1","deviceName":"iPhone da Ana","deviceTag":"t1","appIdentifier":{"@odata.type":"#microsoft.graph.iosAppIdentifier","bundleId":"com.microsoft.teams"},
              "applicationVersion":"7.1","lastSyncDateTime":"2026-10-01T10:00:00Z","flaggedReasons":["rootedDevice"],"appliedPolicies":[{"id":"x","displayName":"iOS - Proteção"}]}]}
            """));
        var regs = new List<MamRegistration>();
        await foreach (var r in new HttpGraphReader(Client(handler)).ReadMamRegistrationsAsync(default)) regs.Add(r);
        var r1 = Assert.Single(regs);
        Assert.Equal(("iOS", "com.microsoft.teams", "u1", "iOS - Proteção", "rootedDevice"), (r1.DeviceType, r1.AppIdentifier, r1.UserId, r1.AppliedPolicies, r1.FlaggedReasons));
    }

    [Fact]
    public async Task UsersAreResolvedInBatch()
    {
        var handler = new StubHandler(_ => Json("""{"responses":[{"id":"0a0a0a0a-0000-0000-0000-000000000001","status":200,"body":{"id":"0a0a0a0a-0000-0000-0000-000000000001","userPrincipalName":"ana@x","displayName":"Ana","department":"TI","accountEnabled":false}},{"id":"0a0a0a0a-0000-0000-0000-000000000002","status":404}]}"""));
        var users = new List<EntraUser>();
        await foreach (var u in new HttpGraphReader(Client(handler)).ReadUsersAsync(["0a0a0a0a-0000-0000-0000-000000000001", "0a0a0a0a-0000-0000-0000-000000000002"], default)) users.Add(u);
        var u1 = Assert.Single(users);
        Assert.Equal(("TI", false), (u1.Department, u1.AccountEnabled));
    }

    private static readonly string Zero = Guid.Empty.ToString();

    [Fact]
    public void GraphIdsKeepOnlyUsableGuidsOnceEach()
    {
        var a = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var clean = GraphIds.Clean([null, "", "   ", Zero, "not-a-guid", a, a.ToLowerInvariant(), "/users/../x", Guid.Parse(a).ToString("B")]);
        Assert.Equal([Guid.Parse(a).ToString("D")], clean);
        Assert.False(GraphIds.IsUsable(null));
        Assert.False(GraphIds.IsUsable(Zero));
        Assert.True(GraphIds.IsUsable(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task UsersWithBlankZeroOrRepeatedIdsNeverReachTheBatch()
    {
        var good = "0a0a0a0a-0000-0000-0000-000000000001";
        var sent = new List<string>();
        var handler = new StubHandler(r =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result);
            var reqs = doc.RootElement.GetProperty("requests").EnumerateArray().Select(x => (Id: x.GetProperty("id").GetString()!, Url: x.GetProperty("url").GetString()!)).ToList();
            sent.AddRange(reqs.Select(q => q.Url));
            return Json("{\"responses\":[" + string.Join(",", reqs.Select(q => Resp(q.Id, 200, "{\"id\":\"" + q.Id + "\",\"userPrincipalName\":\"a@x\"}"))) + "]}");
        });
        var users = new List<EntraUser>();
        await foreach (var u in new HttpGraphReader(Client(handler)).ReadUsersAsync([null!, "", Zero, good, good.ToUpperInvariant(), "x y"], default)) users.Add(u);
        Assert.Single(users);
        Assert.Single(sent);
        Assert.Contains(good, sent[0]);
    }

    [Fact]
    public async Task NoUsableIdMeansNoRequestAtAll()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; return Json("{\"responses\":[]}"); });
        var reader = new HttpGraphReader(Client(handler));
        await foreach (var _ in reader.ReadUsersAsync(["", Zero], default)) { }
        await foreach (var _ in reader.ReadDevicePolicyStatesAsync([Zero, ""], default)) { }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BatchDropsMalformedEntriesInsteadOfLettingGraphRejectTheWholeBatch()
    {
        var sentIds = new List<string>();
        var handler = new StubHandler(r =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result);
            var ids = doc.RootElement.GetProperty("requests").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();
            sentIds.AddRange(ids);
            return Json("{\"responses\":[" + string.Join(",", ids.Select(id => Resp(id, 200, "{}"))) + "]}");
        });
        var client = Client(handler);
        var result = await client.BatchGetAsync([("ok", "/users/1"), ("", "/users/2"), ("dup", "/users/3"), ("dup", "/users/4"), ("hole", "/users//x"), ("abs", "https://evil.test/x"), ("proto", "//evil.test/x"), ("   ", "/users/5")], default);
        Assert.Equal(["ok", "dup"], sentIds);
        Assert.Equal(2, result.Count);
        Assert.Equal(6, client.DroppedRequests);
    }
}

internal static class AsyncExtensions
{
    public static async Task<T> FirstAsync2<T>(this IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
        {
            return item;
        }

        throw new InvalidOperationException("empty");
    }
}

public class IntuneDepthPipelineTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
    private readonly SyntheticEstate _estate = new();
    private readonly FakeTimeProvider _clock = new(SyntheticEstate.Now.AddMinutes(30));
    private readonly SwitchableSources _sources;
    private readonly JobRunner _runner;
    private readonly SettingsProvider _settings;

    public IntuneDepthPipelineTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var paths = new NexusPaths(_dataDir);
        var settings = new NexusSettings();
        settings.Sccm.Mode = SourceMode.Simulated;
        settings.ActiveDirectory.Mode = SourceMode.Simulated;
        new SettingsStore(paths).Save(settings);
        _settings = new SettingsProvider(paths);
        _sources = new SwitchableSources(_estate);
        _runner = new JobRunner(_db, _settings, _sources, new CollectionGate(new FixedLoad(10), _clock), _clock, NullLogger<JobRunner>.Instance);
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
    public async Task EveryJobStoresItsData()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        Assert.Equal(_estate.Policies.Count, await db.IntunePolicies.CountAsync());
        Assert.True(await db.IntuneDevicePolicyStates.CountAsync() > 500);
        Assert.Equal(_estate.MamRegistrations.Count, await db.MamRegistrations.CountAsync());
        Assert.Equal(_estate.Users.Count, await db.EntraUsers.CountAsync());
        Assert.Contains(await db.SccmDevices.ToListAsync(), s => s.CpuName != null && s.MemoryMb > 0 && s.LastHwScanAt != null && s.ClientVersion != null);
        Assert.Contains(await db.IntuneDevices.ToListAsync(), d => d.IsEncrypted == true && d.TotalStorageBytes > 0 && d.UserId != null);
    }

    [Fact]
    public async Task MamAttachesToTheUsersSingleDeviceAndCreatesAssetsForMamOnlyDevices()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        var assets = await db.Assets.ToListAsync();
        var mamOnly = assets.Where(a => a.Coverage == "OnlyMam").ToList();
        Assert.Equal(25, mamOnly.Count);                                    // one device tag per MAM-only user
        Assert.All(mamOnly, a => Assert.True(a.HasMam && a.MamAppCount > 0 && a.Ownership == "Personal" && a.PrimaryUser != null));
        var withBoth = assets.Where(a => a is { HasMam: true, IntuneChannel: "Mdm" }).ToList();
        Assert.True(withBoth.Count > 0);                                          // personal phones with MDM and protected apps
        Assert.All(withBoth, a => Assert.False(string.IsNullOrEmpty(a.MamPolicies)));
        Assert.Contains(await db.AssetLinks.ToListAsync(), l => l is { Source: "mam", Evidence: "user-platform" });
    }

    [Fact]
    public async Task PolicyCountsDepartmentsAndNewRulesReachTheAssets()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        var assets = await db.Assets.ToListAsync();
        Assert.Contains(assets, a => a.PoliciesCollected && a.CompliancePolicies > 0 && a.ConfigProfiles > 0);
        Assert.Contains(assets, a => a.Department != null);
        Assert.Contains(assets, a => a.UserEnabled == false);

        var snapshot = await InventorySnapshotLoader.LoadAsync(_db, _clock, default);
        Assert.True(snapshot.Sources is { Policies: true, Mam: true, Users: true });
        Assert.Contains(snapshot.Views, v => v.Issues.Contains("nobitlocker"));
        Assert.Contains(snapshot.Views, v => v.Issues.Contains("userdis"));
        Assert.Contains(snapshot.Views, v => v.Issues.Contains("nopolicy"));
        Assert.Contains(snapshot.Views, v => v.Issues.Contains("cfgfail"));
        Assert.Contains(snapshot.Views, v => v.Issues.Contains("rooted"));
        var highlights = snapshot.Overview().Highlights;
        Assert.Equal(KpiState.Available, highlights.Single(k => k.Key == "cripto").State);
        Assert.Equal(KpiState.Available, highlights.Single(k => k.Key == "byod").State);
    }

    [Fact]
    public async Task MamOnlyDevicesAreProtectedAndNotFlaggedAsUnprotected()
    {
        await CollectAllAsync();
        var snapshot = await InventorySnapshotLoader.LoadAsync(_db, _clock, default);
        var mamOnly = snapshot.Views.Where(v => v.Asset.Coverage == "OnlyMam").ToList();
        Assert.True(mamOnly.Count > 0);
        Assert.All(mamOnly, v => Assert.DoesNotContain("byodnoprot", v.Issues));
        Assert.All(mamOnly, v => Assert.Equal(Management.OnlyMam, v.Management));
    }

    [Fact]
    public async Task OnDemandInventoryFetchStoresSoftwareFromSccmAndIntune()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        var both = await db.AssetLinks.Where(l => l.Source == "sccm").Select(l => l.AssetId)
            .Intersect(db.AssetLinks.Where(l => l.Source == "intune").Select(l => l.AssetId)).FirstAsync();

        var fetcher = new InventoryFetcher(_db, _settings, _sources, _clock, NullLogger<InventoryFetcher>.Instance);
        var message = await fetcher.FetchAsync(both, default);
        Assert.Contains("programas", message);
        var rows = await db.InstalledSoftware.Where(r => r.AssetId == both).ToListAsync();
        Assert.Contains(rows, r => r.Source == "sccm" && r.Name.Contains("Microsoft Edge"));
        Assert.Contains(rows, r => r.Source == "intune");
        Assert.Equal("Done", (await db.InventoryFetches.SingleAsync(f => f.AssetId == both)).Status);

        await fetcher.FetchAsync(both, default); // a second fetch replaces, never duplicates
        Assert.Equal(rows.Count, await db.InstalledSoftware.CountAsync(r => r.AssetId == both));
    }

    [Fact]
    public async Task PolicyFailureKeepsThePreviousSnapshot()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        var before = await db.IntuneDevicePolicyStates.CountAsync();
        _sources.FailGraph = true;
        Assert.Equal(JobRunner.StatusFailed, (await _runner.RunAsync(JobNames.Policies, default)).Status);
        Assert.Equal(before, await db.IntuneDevicePolicyStates.CountAsync());
    }
}
