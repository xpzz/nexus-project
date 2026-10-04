using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Nexus.Collectors.Graph;
using Nexus.Core.Azure;

namespace Nexus.Tests;

public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Urls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Urls.Add(request.RequestUri!.ToString());
        return Task.FromResult(respond(request));
    }
}

public sealed class StaticToken : ITokenProvider
{
    public int Invalidations { get; private set; }
    public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token-" + Invalidations);
    public void Invalidate() => Invalidations++;
}

public class GraphClientTests
{
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GraphHttpClient Client(StubHandler handler, List<TimeSpan>? delays = null, StaticToken? token = null) =>
        new(new HttpClient(handler), token ?? new StaticToken(), (d, _) => { delays?.Add(d); return Task.CompletedTask; });

    [Fact]
    public async Task FollowsNextLinkAcrossPages()
    {
        var handler = new StubHandler(r => r.RequestUri!.ToString().Contains("page2")
            ? Json("""{"value":[{"id":"3"}]}""")
            : Json("""{"value":[{"id":"1"},{"id":"2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/devices?page2"}"""));
        var ids = new List<string>();
        await foreach (var item in Client(handler).GetPagedAsync("/devices", default))
        {
            ids.Add(item.GetProperty("id").GetString()!);
        }

        Assert.Equal(["1", "2", "3"], ids);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task ThrottlingHonorsRetryAfterAndRetries()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            if (++calls == 1)
            {
                var throttled = Json("{}", HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return throttled;
            }

            return Json("""{"value":[]}""");
        });
        var delays = new List<TimeSpan>();
        using var doc = await Client(handler, delays).GetAsync("/devices", default);
        Assert.Equal(2, calls);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task UnauthorizedRefreshesTheTokenOnce()
    {
        var token = new StaticToken();
        var handler = new StubHandler(r => r.Headers.Authorization!.Parameter == "token-0" ? Json("{}", HttpStatusCode.Unauthorized) : Json("""{"value":[]}"""));
        using var doc = await Client(handler, token: token).GetAsync("/devices", default);
        Assert.Equal(1, token.Invalidations);
    }

    [Fact]
    public async Task ForbiddenIsTranslatedWithImpactAndFix()
    {
        var handler = new StubHandler(_ => Json("""{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<GraphException>(() => Client(handler).GetAsync("/deviceManagement/managedDevices", default));
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
        Assert.False(string.IsNullOrWhiteSpace(ex.Error.Impact));
        Assert.False(string.IsNullOrWhiteSpace(ex.Error.HowToFix));
    }

    [Fact]
    public async Task PersistentServerErrorsStopAfterMaxAttempts()
    {
        var handler = new StubHandler(_ => Json("{}", HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<GraphException>(() => Client(handler).GetAsync("/devices", default));
        Assert.Equal(6, handler.Urls.Count);
    }

    [Fact]
    public async Task ReaderMapsFieldsAndTreatsEmptyGuidAsMissing()
    {
        const string body = """
            {"value":[
              {"id":"m1","deviceName":"PC1","azureADDeviceId":"00000000-0000-0000-0000-000000000000","serialNumber":"ABC","managementAgent":"mdm","managedDeviceOwnerType":"company","lastSyncDateTime":"2026-10-01T10:00:00Z","complianceState":"compliant"},
              {"id":"m2","deviceName":"PC2","azureADDeviceId":"11111111-1111-1111-1111-111111111111"}]}
            """;
        var reader = new HttpGraphReader(Client(new StubHandler(_ => Json(body))));
        var devices = new List<IntuneManagedDevice>();
        await foreach (var d in reader.ReadManagedDevicesAsync(default))
        {
            devices.Add(d);
        }

        Assert.Null(devices[0].AzureAdDeviceId);
        Assert.Equal("mdm", devices[0].ManagementAgent);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), devices[0].LastSyncAt);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), devices[1].AzureAdDeviceId);
    }

    [Fact]
    public void ReaderUrlsRequestOnlyTheFieldsTheProductUses()
    {
        Assert.Contains("$select=", HttpGraphReader.ManagedDevicesUrl);
        Assert.DoesNotContain("imei", HttpGraphReader.ManagedDevicesUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phoneNumber", HttpGraphReader.ManagedDevicesUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$select=", HttpGraphReader.EntraDevicesUrl);
    }
}

public class GraphAssertionTests
{
    [Fact]
    public void ClientAssertionIsAValidRs256JwtWithThumbprintHeader()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=nexus-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        var jwt = GraphAssertion.Create(certificate, "tenant-1", "client-1", now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        using var header = JsonDocument.Parse(GraphAssertion.FromBase64Url(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.True(header.RootElement.TryGetProperty("x5t", out _));
        using var payload = JsonDocument.Parse(GraphAssertion.FromBase64Url(parts[1]));
        Assert.Equal("client-1", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal("client-1", payload.RootElement.GetProperty("sub").GetString());
        Assert.Contains("tenant-1", payload.RootElement.GetProperty("aud").GetString());

        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        Assert.True(rsa.VerifyData(signed, GraphAssertion.FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void RolesAreReadFromTheTokenAndComparedWithRequiredPermissions()
    {
        static string Part(string json) => GraphAssertion.Base64Url(Encoding.UTF8.GetBytes(json));
        var token = $"{Part("""{"alg":"none"}""")}.{Part("""{"roles":["Device.Read.All","User.Read.All"]}""")}.sig";
        var roles = GraphAssertion.Roles(token);
        Assert.Equal(["Device.Read.All", "User.Read.All"], roles);

        var missing = GraphPermissions.Missing(roles, GraphPermissions.RequiredUpTo(1));
        Assert.Contains(missing, p => p.Name == "DeviceManagementManagedDevices.Read.All");
        Assert.DoesNotContain(missing, p => p.Name == "Device.Read.All");
    }
}
