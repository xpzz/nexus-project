using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nexus.Cli;
using Nexus.Core.Configuration;
using Nexus.Web.Hosting;

namespace Nexus.Tests;

public class HttpsCertificateLoaderTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable(HttpsCertificateLoader.StoreLocationVariable);
    private X509Certificate2? _installed;

    public HttpsCertificateLoaderTests() =>
        Environment.SetEnvironmentVariable(HttpsCertificateLoader.StoreLocationVariable, "CurrentUser");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingThumbprintExplainsWhatHappenedImpactAndFix(string? thumbprint)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HttpsCertificateLoader.Load(thumbprint));
        Assert.Contains("não está configurado", ex.Message);
        Assert.Contains("Impacto:", ex.Message);
        Assert.Contains("Como resolver:", ex.Message);
    }

    [Fact]
    public void UnknownThumbprintSaysTheCertificateWasNotFound()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HttpsCertificateLoader.Load("AA BB CC"));
        Assert.Contains("AABBCC", ex.Message);
        Assert.Contains("não foi encontrado", ex.Message);
        Assert.Contains("Como resolver:", ex.Message);
    }

    [Fact]
    public void FindsTheCertificateByThumbprintIgnoringCaseAndSpaces()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=nexus.teste.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Re-import so the private key is persisted with the certificate (as it is in a real store).
        _installed = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "t"), "t", X509KeyStorageFlags.PersistKeySet);
        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(_installed);
        }

        var spaced = string.Join(" ", Enumerable.Range(0, _installed.Thumbprint.Length / 2).Select(i => _installed.Thumbprint.Substring(i * 2, 2))).ToLowerInvariant();
        using var loaded = HttpsCertificateLoader.Load(spaced);
        Assert.Equal(_installed.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void InstallAnswersCarryHostingPortAndThumbprintIntoTheSettings()
    {
        var answers = new InstallAnswers { Hosting = "service", HttpsPort = 9443, CertificateThumbprint = "ABCDEF" };
        var settings = answers.ApplyTo(new NexusSettings());
        Assert.Equal("service", settings.Web.Hosting);
        Assert.Equal(9443, settings.Web.HttpsPort);
        Assert.Equal("ABCDEF", settings.Web.CertificateThumbprint);

        var unchanged = new InstallAnswers().ApplyTo(new NexusSettings());
        Assert.Equal("service", unchanged.Web.Hosting);
        Assert.Equal(8443, unchanged.Web.HttpsPort);
        Assert.Null(unchanged.Web.CertificateThumbprint);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HttpsCertificateLoader.StoreLocationVariable, _previous);
        if (_installed is not null)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(_installed);
            _installed.Dispose();
        }
    }
}
