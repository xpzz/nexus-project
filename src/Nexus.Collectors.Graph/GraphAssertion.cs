using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Nexus.Collectors.Graph;

/// <summary>Client assertion (RS256 JWT) signed with the certificate private key: how the app authenticates without a secret.</summary>
public static class GraphAssertion
{
    public static string Create(X509Certificate2 certificate, string tenantId, string clientId, DateTimeOffset now)
    {
        var header = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["x5t"] = Base64Url(certificate.GetCertHash()),
        });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = Guid.NewGuid().ToString(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
        });
        var signingInput = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("Certificado sem chave privada acessível.");
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        return Convert.FromBase64String(padded);
    }

    /// <summary>"roles" claim of an access token (application permissions granted), without validating the signature.</summary>
    public static IReadOnlyList<string> Roles(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(FromBase64Url(parts[1]));
        return doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
            ? roles.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0).ToList()
            : [];
    }
}
