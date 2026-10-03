using System.Text.Json;

namespace Nexus.Core.Configuration;

/// <summary>
/// Written by deploy/Install-AzulNexusAzure.ps1 to config/azure.json. Identifiers and certificate thumbprints only:
/// the Entra ID applications authenticate with certificates, so there is no client secret anywhere.
/// </summary>
public sealed class AzureSettings
{
    public int SchemaVersion { get; set; }
    public string TenantId { get; set; } = "";
    public string? TenantName { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public AzureApp Collector { get; set; } = new();
    public AzureWebApp Web { get; set; } = new();
    public List<string> Permissions { get; set; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(Collector.ClientId);

    public sealed class AzureApp
    {
        public string AppName { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ObjectId { get; set; } = "";
        public string CertificateThumbprint { get; set; } = "";
        public string CertificateSubject { get; set; } = "";
    }

    public sealed class AzureWebApp
    {
        public string AppName { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ObjectId { get; set; } = "";
        public string CertificateThumbprint { get; set; } = "";
        public string CertificateSubject { get; set; } = "";
        public string RedirectUri { get; set; } = "";
        public string LogoutUri { get; set; } = "";
        public List<string> Roles { get; set; } = [];
    }
}

public static class AzureSettingsStore
{
    /// <summary>Null when the Azure step has not been run yet (the wizard shows it as "Pendente").</summary>
    public static AzureSettings? Load(NexusPaths paths)
    {
        var file = Path.Combine(paths.ConfigDirectory, "azure.json");
        if (!File.Exists(file))
        {
            return null;
        }

        using var stream = File.OpenRead(file);
        var settings = JsonSerializer.Deserialize<AzureSettings>(stream, Configuration.SettingsStore.JsonOptions);
        return settings is { IsConfigured: true } ? settings : null;
    }
}
