using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nexus.Core.Security;

/// <summary>Finds a client certificate (with usable private key) by thumbprint in the machine store.</summary>
public static class CertificateLookup
{
    /// <summary>Development/test knob: "CurrentUser" reads the current user's store (default: LocalMachine).</summary>
    public const string StoreLocationVariable = "NEXUS_CERT_STORE_LOCATION";

    public static X509Certificate2 Find(string? thumbprint, string purpose)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new InvalidOperationException(
                $"O certificado de {purpose} não está configurado. Impacto: a coleta que depende dele não roda. " +
                "Como resolver: rode Configurar-Azure.cmd no servidor para criar os registros e os certificados.");
        }

        var location = string.Equals(Environment.GetEnvironmentVariable(StoreLocationVariable), "CurrentUser", StringComparison.OrdinalIgnoreCase)
            ? StoreLocation.CurrentUser
            : StoreLocation.LocalMachine;
        var normalized = new string(thumbprint.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"O certificado de {purpose} ({normalized}) não está em {location}\\My. Impacto: a coleta que depende dele não roda. " +
                "Como resolver: rode Configurar-Azure.cmd de novo no servidor (ele recria o certificado e o registro).");
        }

        var certificate = matches[0];
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is null)
            {
                throw new CryptographicException("Certificado sem chave privada.");
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"A conta do serviço não consegue usar a chave privada do certificado de {purpose} ({ex.Message}). Impacto: a coleta que depende dele não roda. " +
                "Como resolver: rode Configurar-Azure.cmd (ele dá leitura da chave à conta do serviço) ou use certlm.msc › Gerenciar Chaves Privadas.", ex);
        }

        return certificate;
    }
}
