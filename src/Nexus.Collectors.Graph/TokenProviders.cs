using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Nexus.Collectors.Graph;

public interface ITokenProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached token (after a 401).</summary>
    void Invalidate();
}

/// <summary>Client credentials with a certificate. The token is cached until shortly before it expires.</summary>
public sealed class CertificateTokenProvider(string tenantId, string clientId, X509Certificate2 certificate, HttpClient http, TimeProvider? clock = null) : ITokenProvider
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && _clock.GetUtcNow() < _expiresAt - TimeSpan.FromMinutes(5))
            {
                return _token;
            }

            var now = _clock.GetUtcNow();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                    ["client_assertion"] = GraphAssertion.Create(certificate, tenantId, clientId, now),
                }),
            };
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new GraphException(response.StatusCode, GraphErrors.FromToken(body));
            }

            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.GetProperty("access_token").GetString()!;
            _expiresAt = now.AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3000);
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate() => _token = null;
}
