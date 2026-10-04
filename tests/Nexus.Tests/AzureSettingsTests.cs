using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.Core.Azure;
using Nexus.Core.Configuration;

namespace Nexus.Tests;

public class AzureSettingsTests
{
    // Same shape that Install-AzulNexusAzure.ps1 writes (New-AzureConfig).
    private const string Sample = """
        {
          "schemaVersion": 1,
          "tenantId": "11111111-1111-1111-1111-111111111111",
          "tenantName": "Azul",
          "createdAt": "2026-10-03T18:00:00.0000000+00:00",
          "collector": { "appName": "Azul Nexus – Coletor", "clientId": "c-1", "objectId": "o-1", "certificateThumbprint": "AA11", "certificateSubject": "CN=AzulNexus-Coletor" },
          "web": { "appName": "Azul Nexus – Web", "clientId": "w-1", "objectId": "o-2", "certificateThumbprint": "BB22", "certificateSubject": "CN=AzulNexus-Web",
                   "redirectUri": "https://nexus.azul.corp:8443/signin-oidc", "logoutUri": "https://nexus.azul.corp:8443/signout-oidc",
                   "roles": ["Nexus.Leitura", "Nexus.Analista", "Nexus.AdminIntegracao", "Nexus.Auditoria"] },
          "permissions": ["DeviceManagementManagedDevices.Read.All", "Device.Read.All"]
        }
        """;

    [Fact]
    public void LoadsTheFileWrittenByTheAzureScript()
    {
        var paths = new NexusPaths(Path.Combine(Path.GetTempPath(), "nexus-azure-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "azure.json"), Sample);

        var settings = AzureSettingsStore.Load(paths);

        Assert.NotNull(settings);
        Assert.True(settings!.IsConfigured);
        Assert.Equal("c-1", settings.Collector.ClientId);
        Assert.Equal("BB22", settings.Web.CertificateThumbprint);
        Assert.Contains("Nexus.AdminIntegracao", settings.Web.Roles);
        Directory.Delete(paths.DataDirectory, recursive: true);
    }

    [Fact]
    public void ReturnsNullWhenTheAzureStepWasNotRun()
    {
        var paths = new NexusPaths(Path.Combine(Path.GetTempPath(), "nexus-azure-" + Guid.NewGuid().ToString("N")));
        Assert.Null(AzureSettingsStore.Load(paths));
    }

    [Fact]
    public void ScriptPermissionListMatchesTheDomainList()
    {
        // The PowerShell script and GraphPermissions (SPEC 4.5) must ask for exactly the same application permissions.
        var script = File.ReadAllText(FindRepoFile("deploy/lib/Azure.ps1"));
        var inScript = Regex.Matches(script, @"@\{ Name = '([A-Za-z.]+)'; Optional = \$(true|false);")
            .Select(m => (Name: m.Groups[1].Value, Optional: m.Groups[2].Value == "true"))
            .OrderBy(p => p.Name).ToList();
        var inDomain = GraphPermissions.Collector.Select(p => (p.Name, p.Optional)).OrderBy(p => p.Name).ToList();
        Assert.Equal(inDomain, inScript);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AzulNexus.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada."), relative);
    }
}
