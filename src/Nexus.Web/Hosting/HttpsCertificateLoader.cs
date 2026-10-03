using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nexus.Web.Hosting;

/// <summary>
/// Loads the HTTPS certificate for "service" hosting from the machine store. Errors say what happened,
/// the impact and how to fix it (they end up in the service log and in the Windows Event Log).
/// </summary>
public static class HttpsCertificateLoader
{
    /// <summary>Development/test knob: "CurrentUser" reads the current user's store (default: LocalMachine).</summary>
    public const string StoreLocationVariable = "NEXUS_CERT_STORE_LOCATION";

    public static X509Certificate2 Load(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new InvalidOperationException(
                "O certificado HTTPS não está configurado (web.certificateThumbprint em nexus.json). " +
                "Impacto: o site não consegue abrir a porta HTTPS e o serviço para. " +
                "Como resolver: rode o instalador de novo (Instalar.cmd) ou informe a impressão digital do certificado em iis.certificate no install.json.");
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
                $"O certificado {normalized} não foi encontrado em {location}\\My. " +
                "Impacto: o site não consegue abrir a porta HTTPS e o serviço para. " +
                "Como resolver: importe o certificado (com chave privada) no repositório do computador ou rode o instalador de novo para escolher outro.");
        }

        var certificate = matches[0];
        try
        {
            // Forces the private key access check now, with the service identity, instead of failing at the first handshake.
            using var key = certificate.GetRSAPrivateKey() as IDisposable;
            if (key is null && certificate.GetECDsaPrivateKey() is null)
            {
                throw new CryptographicException("Certificado sem chave privada.");
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"O certificado {normalized} não pôde usar a chave privada com a conta do serviço ({ex.Message}). " +
                "Impacto: o site não consegue abrir a porta HTTPS e o serviço para. " +
                "Como resolver: dê permissão de leitura da chave privada à conta do serviço (certlm.msc › Todas as Tarefas › Gerenciar Chaves Privadas) ou rode o instalador de novo, que concede esse acesso.", ex);
        }

        return certificate;
    }
}
