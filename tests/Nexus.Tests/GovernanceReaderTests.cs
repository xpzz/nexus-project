using System.Net;
using System.Text;
using Nexus.Collectors.Graph;

namespace Nexus.Tests;

public class GovernanceReaderTests
{
    private const string P1 = "11111111-1111-1111-1111-111111111111";
    private const string G1 = "22222222-2222-2222-2222-222222222222";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Resp(string id, int status, string? body = null) => "{\"id\":\"" + id + "\",\"status\":" + status + (body is null ? "" : ",\"body\":" + body) + "}";

    private static HttpGovernanceReader Reader(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new GraphHttpClient(new HttpClient(new StubHandler(handler)), new StaticToken(), (_, _) => Task.CompletedTask));

    private static async Task<List<T>> All<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var x in source) list.Add(x);
        return list;
    }

    private static HttpResponseMessage ProtectionApi(HttpRequestMessage r)
    {
        var path = r.RequestUri!.AbsolutePath;
        if (r.Method == HttpMethod.Post)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result);
            var reqs = doc.RootElement.GetProperty("requests").EnumerateArray().Select(x => (Id: x.GetProperty("id").GetString()!, Url: x.GetProperty("url").GetString()!)).ToList();
            var items = reqs.Select(q => q.Url switch
            {
                var u when u.EndsWith("/apps") => Resp(q.Id, 200, "{\"value\":[{\"mobileAppIdentifier\":{\"@odata.type\":\"#microsoft.graph.iosMobileAppIdentifier\",\"bundleId\":\"com.microsoft.office.outlook\"}},{\"mobileAppIdentifier\":{\"bundleId\":\"com.microsoft.msedge\"}}]}"),
                var u when u.EndsWith("/assignments") => Resp(q.Id, 200, "{\"value\":[{\"target\":{\"@odata.type\":\"#microsoft.graph.groupAssignmentTarget\",\"groupId\":\"" + G1 + "\"}}]}"),
                var u when u.StartsWith("/groups/") => Resp(q.Id, 200, "{\"id\":\"" + G1 + "\",\"displayName\":\"BYOD - Celulares\"}"),
                _ => Resp(q.Id, 404),
            });
            return Json("{\"responses\":[" + string.Join(",", items) + "]}");
        }

        if (path.EndsWith("/iosManagedAppProtections"))
        {
            return Json("{\"value\":[{\"id\":\"" + P1 + "\",\"displayName\":\"iOS - Proteção BYOD\",\"version\":\"7\",\"lastModifiedDateTime\":\"2026-09-01T10:00:00Z\"," +
                "\"allowedOutboundClipboardSharingLevel\":\"managedAppsWithPasteIn\",\"allowedOutboundDataTransferDestinations\":\"managedApps\",\"allowedInboundDataTransferSources\":\"managedApps\"," +
                "\"dataBackupBlocked\":true,\"pinRequired\":true,\"minimumPinLength\":6,\"saveAsBlocked\":true,\"allowedDataStorageLocations\":[\"oneDriveForBusiness\",\"sharePoint\"],\"appDataEncryptionType\":\"whenDeviceLocked\",\"ignoredProperty\":\"x\"}]}");
        }

        return Json("{\"error\":{\"code\":\"NotFound\",\"message\":\"x\"}}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ProtectionPoliciesCarrySettingsAppsAndReadableAssignments()
    {
        var policies = await All(Reader(ProtectionApi).ReadAppProtectionPoliciesAsync(default));
        var p = Assert.Single(policies); // Android path answered 404: tenant without it is not an error
        Assert.Equal(("iOS", "iOS - Proteção BYOD", 7, true, false), (p.Platform, p.Name, p.Version, p.IsAssigned, p.AssignedToAll));
        Assert.Equal("Grupo: BYOD - Celulares", p.Assignments);
        Assert.Equal(["com.microsoft.office.outlook", "com.microsoft.msedge"], p.Apps);
        Assert.Equal("managedAppsWithPasteIn", p.Settings["allowedOutboundClipboardSharingLevel"]);
        Assert.Equal("oneDriveForBusiness,sharePoint", p.Settings["allowedDataStorageLocations"]);
        Assert.Equal("true", p.Settings["dataBackupBlocked"]);
        Assert.False(p.Settings.ContainsKey("ignoredProperty")); // only the settings that govern data are kept
    }

    [Fact]
    public async Task ManagedAppAndManagedDeviceConfigurationsExposeEdgeSettings()
    {
        var handler = (HttpRequestMessage r) =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Post)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result);
                var reqs = doc.RootElement.GetProperty("requests").EnumerateArray().Select(x => (Id: x.GetProperty("id").GetString()!, Url: x.GetProperty("url").GetString()!)).ToList();
                return Json("{\"responses\":[" + string.Join(",", reqs.Select(q => q.Url.EndsWith("/apps") ? Resp(q.Id, 200, "{\"value\":[{\"mobileAppIdentifier\":{\"bundleId\":\"com.microsoft.msedge\"}}]}") : Resp(q.Id, 200, "{\"value\":[]}"))) + "]}");
            }

            if (path.EndsWith("/targetedManagedAppConfigurations"))
            {
                return Json("{\"value\":[{\"id\":\"" + P1 + "\",\"displayName\":\"Edge iOS\",\"customSettings\":[{\"name\":\"com.microsoft.intune.mam.managedbrowser.BlockListURLs\",\"value\":\"a.com|b.com|c.com\"}]}]}");
            }

            if (path.EndsWith("/mobileAppConfigurations"))
            {
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"URLBlocklist\":\"[\\\"x.com\\\",\\\"y.com\\\"]\",\"URLAllowlist\":\"corp.azul.com\"}"));
                return Json("{\"value\":[{\"@odata.type\":\"#microsoft.graph.androidManagedStoreAppConfiguration\",\"id\":\"cfg1\",\"displayName\":\"Edge Android\",\"payloadJson\":\"" + payload + "\",\"assignments\":[{\"target\":{\"@odata.type\":\"#microsoft.graph.allLicensedUsersAssignmentTarget\"}}]}]}");
            }

            return Json("{\"value\":[]}");
        };
        var configs = await All(Reader(handler).ReadAppConfigurationsAsync(default));

        var managedApp = Assert.Single(configs, c => c.Kind == "managed-app");
        Assert.Equal("iOS", managedApp.Platform);
        Assert.Equal("a.com|b.com|c.com", managedApp.Settings["com.microsoft.intune.mam.managedbrowser.BlockListURLs"]);

        var device = Assert.Single(configs, c => c.Kind == "managed-device");
        Assert.Equal("Android", device.Platform);
        Assert.Equal("Todos os usuários", device.Assignments);
        Assert.Equal("corp.azul.com", device.Settings["URLAllowlist"]);
        Assert.Contains("y.com", device.Settings["URLBlocklist"]);
    }

    [Fact]
    public void PayloadParsingAcceptsPlainBase64AndKeyedArrays()
    {
        Assert.Equal("1", HttpGovernanceReader.ParsePayload("{\"a\":1}").Single().Value);
        Assert.Equal("v", HttpGovernanceReader.ParsePayload(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"k\":\"v\"}"))).Single().Value);
        Assert.Equal("z", HttpGovernanceReader.ParsePayload("[{\"key\":\"URLBlocklist\",\"valueString\":\"z\"}]").Single().Value);
        Assert.Empty(HttpGovernanceReader.ParsePayload("not json or base64 !!"));
    }

    [Fact]
    public async Task ConditionalAccessPoliciesFlagWhatTheyRequire()
    {
        var reader = Reader(_ => Json("{\"value\":[" +
            "{\"id\":\"c1\",\"displayName\":\"Exigir app protegido\",\"state\":\"enabled\",\"conditions\":{\"applications\":{\"includeApplications\":[\"Office365\"]},\"users\":{\"includeUsers\":[\"All\"]},\"platforms\":{\"includePlatforms\":[\"android\",\"iOS\"]}},\"grantControls\":{\"builtInControls\":[\"approvedApplication\",\"compliantApplication\"]}}," +
            "{\"id\":\"c2\",\"displayName\":\"Exigir compliant\",\"state\":\"enabledForReportingButNotEnforced\",\"conditions\":{\"applications\":{\"includeApplications\":[\"All\"]},\"users\":{\"includeGroups\":[\"g1\",\"g2\"]}},\"grantControls\":{\"builtInControls\":[\"compliantDevice\",\"mfa\"]}}," +
            "{\"id\":\"c3\",\"displayName\":\"Sem controles\",\"state\":\"disabled\"}]}"));
        var list = await All(reader.ReadConditionalAccessAsync(default));
        var c1 = list.Single(x => x.Id == "c1");
        Assert.True(c1.RequiresApprovedApp && c1.RequiresAppProtection && c1.TargetsMicrosoft365 && !c1.RequiresCompliantDevice);
        Assert.Equal("Todos os usuários", c1.Users);
        Assert.Equal("android, iOS", c1.Platforms);
        var c2 = list.Single(x => x.Id == "c2");
        Assert.True(c2.RequiresCompliantDevice && c2.RequiresMfa && c2.TargetsMicrosoft365);
        Assert.Equal("2 grupo(s)", c2.Users);
        Assert.Equal("enabledForReportingButNotEnforced", c2.State);
        Assert.False(list.Single(x => x.Id == "c3").TargetsMicrosoft365);
    }

    private static string SignIn(string user, string? device, string os, string resource, string when, bool? compliant = true) =>
        "{\"userId\":\"" + user + "\",\"userPrincipalName\":\"" + user + "@azul.corp\",\"resourceDisplayName\":\"" + resource + "\",\"clientAppUsed\":\"Mobile Apps and Desktop clients\",\"createdDateTime\":\"" + when + "\"," +
        "\"deviceDetail\":{\"deviceId\":" + (device is null ? "\"\"" : "\"" + device + "\"") + ",\"displayName\":\"" + (device is null ? "" : "PHONE") + "\",\"operatingSystem\":\"" + os + "\",\"browser\":\"Edge Mobile\",\"isManaged\":false,\"isCompliant\":" + (compliant?.ToString().ToLowerInvariant() ?? "null") + ",\"trustType\":\"Workplace\"}}";

    [Fact]
    public async Task SignInsAreSummarizedPerDeviceWithWorkloadsAndTheLatestDate()
    {
        var dev = "33333333-3333-3333-3333-333333333333";
        var reader = Reader(_ => Json("{\"value\":[" +
            SignIn("u1", dev, "iOS", "Office 365 Exchange Online", "2026-09-30T08:00:00Z") + "," +
            SignIn("u1", dev, "iOS", "Office 365 SharePoint Online", "2026-10-01T09:00:00Z") + "," +
            SignIn("u2", null, "Android", "Microsoft Teams Services", "2026-10-01T07:00:00Z") + "," +
            SignIn("u2", "00000000-0000-0000-0000-000000000000", "Android", "Microsoft Teams Services", "2026-10-01T08:00:00Z") + "]}"));
        var list = await All(reader.ReadSignInAccessAsync(DateTimeOffset.Parse("2026-09-20T00:00:00Z"), 5, default));

        var byDevice = Assert.Single(list, a => a.EntraDeviceId == dev);
        Assert.Equal(2, byDevice.Count);
        Assert.Equal("Exchange,SharePoint", byDevice.Workloads);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T09:00:00Z"), byDevice.LastAccessAt);
        Assert.Equal("u1", byDevice.UserId);

        var anonymous = Assert.Single(list, a => a.EntraDeviceId is null); // blank and zero device ids both mean "no device"
        Assert.StartsWith("anon:", anonymous.Key);
        Assert.Equal(2, anonymous.Count);
        Assert.Equal("Teams", anonymous.Workloads);
    }

    [Fact]
    public async Task SignInsFallBackToInteractiveWhenTheEventTypeFilterIsRejected()
    {
        var urls = new List<string>();
        var reader = Reader(r =>
        {
            urls.Add(Uri.UnescapeDataString(r.RequestUri!.Query));
            return Uri.UnescapeDataString(r.RequestUri.Query).Contains("signInEventTypes")
                ? Json("{\"error\":{\"code\":\"BadRequest\",\"message\":\"x\"}}", HttpStatusCode.BadRequest)
                : Json("{\"value\":[" + SignIn("u1", null, "iOS", "Office 365 Exchange Online", "2026-10-01T09:00:00Z") + "]}");
        });
        var list = await All(reader.ReadSignInAccessAsync(DateTimeOffset.Parse("2026-09-20T00:00:00Z"), 5, default));
        Assert.Single(list);
        Assert.Equal(2, urls.Count);
        Assert.DoesNotContain("signInEventTypes", urls[1]);
    }

    [Fact]
    public async Task SignInPagesAreCapped()
    {
        var calls = 0;
        var reader = Reader(_ =>
        {
            calls++;
            return Json("{\"value\":[" + SignIn("u1", null, "iOS", "Office 365 Exchange Online", "2026-10-01T09:00:00Z") + "],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/auditLogs/signIns?page=" + calls + "\"}");
        });
        await All(reader.ReadSignInAccessAsync(DateTimeOffset.Parse("2026-09-20T00:00:00Z"), 3, default));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ForbiddenReadsNameThePermissionAndTheLicense()
    {
        var reader = Reader(_ => Json("{\"error\":{\"code\":\"Forbidden\",\"message\":\"denied\"}}", HttpStatusCode.Forbidden));

        var signIns = await Assert.ThrowsAsync<GraphException>(async () => await All(reader.ReadSignInAccessAsync(DateTimeOffset.UtcNow.AddDays(-7), 2, default)));
        Assert.Contains("AuditLog.Read.All", signIns.Message);
        Assert.Contains("Entra ID P1", signIns.Message);

        var ca = await Assert.ThrowsAsync<GraphException>(async () => await All(reader.ReadConditionalAccessAsync(default)));
        Assert.Contains("Policy.Read.All", ca.Message);

        var apps = await Assert.ThrowsAsync<GraphException>(async () => await All(reader.ReadAppProtectionPoliciesAsync(default)));
        Assert.Contains("DeviceManagementApps.Read.All", apps.Message);
        Assert.DoesNotContain("eyJ", apps.Message);
    }

    [Fact]
    public async Task MamRegistrationsKeepTheLatestOperationAndFallBackWhenExpandIsRejected()
    {
        var seen = new List<string>();
        var handler = new StubHandler(r =>
        {
            var query = Uri.UnescapeDataString(r.RequestUri!.Query);
            seen.Add(query);
            if (query.Contains("operations"))
            {
                return Json("{\"error\":{\"code\":\"BadRequest\",\"message\":\"no\"}}", HttpStatusCode.BadRequest);
            }

            return Json("{\"value\":[{\"@odata.type\":\"#microsoft.graph.iosManagedAppRegistration\",\"id\":\"r1\",\"userId\":\"" + P1 + "\",\"deviceTag\":\"t\",\"appliedPolicies\":[{\"displayName\":\"iOS - Proteção BYOD\"}]}]}");
        });
        var reader = new HttpGraphReader(new GraphHttpClient(new HttpClient(handler), new StaticToken(), (_, _) => Task.CompletedTask));
        var regs = await All(reader.ReadMamRegistrationsAsync(default));
        var r1 = Assert.Single(regs);
        Assert.Equal("iOS - Proteção BYOD", r1.AppliedPolicies);
        Assert.Null(r1.LastOperation);
        Assert.Contains(seen, q => q.Contains("operations")); // tried first
        Assert.Contains(seen, q => q.Contains("appliedPolicies") && !q.Contains("operations")); // then without
    }

    [Fact]
    public async Task MamRegistrationsMapTheLastWipeOperation()
    {
        var handler = new StubHandler(_ => Json("{\"value\":[{\"@odata.type\":\"#microsoft.graph.androidManagedAppRegistration\",\"id\":\"r1\",\"userId\":\"" + P1 + "\"," +
            "\"operations\":[{\"displayName\":\"Limpeza seletiva\",\"state\":\"pending\",\"lastModifiedDateTime\":\"2026-09-01T10:00:00Z\"},{\"displayName\":\"Limpeza seletiva\",\"state\":\"done\",\"lastModifiedDateTime\":\"2026-09-20T10:00:00Z\"}]}]}"));
        var reader = new HttpGraphReader(new GraphHttpClient(new HttpClient(handler), new StaticToken(), (_, _) => Task.CompletedTask));
        var r1 = Assert.Single(await All(reader.ReadMamRegistrationsAsync(default)));
        Assert.StartsWith("Limpeza seletiva|done|2026-09-20", r1.LastOperation);
    }
}
