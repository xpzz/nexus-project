using Nexus.Core.Configuration;

namespace Nexus.Cli;

/// <summary>
/// Install file (deploy/install.sample.json) after Install-AzulNexus.ps1 resolved every "auto" value.
/// IIS fields are used by the script; nexusctl only maps what belongs to the application.
/// </summary>
public sealed class InstallAnswers
{
    public string? InstallDir { get; set; }
    public string? DataDir { get; set; }
    public InstallDatabase Database { get; set; } = new();
    public InstallSccm Sccm { get; set; } = new();
    public InstallActiveDirectory ActiveDirectory { get; set; } = new();
    public CollectionSettings? Collection { get; set; }
    public string? PublicUrl { get; set; }
    public string? Hosting { get; set; }
    public int? HttpsPort { get; set; }
    public string? CertificateThumbprint { get; set; }
    public bool DemoMode { get; set; }

    public sealed class InstallDatabase
    {
        public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;
        public string ConnectionString { get; set; } = "";
    }

    public sealed class InstallSccm
    {
        public SourceMode Mode { get; set; } = SourceMode.Disabled;
        public string SiteCode { get; set; } = "";
        public string SqlServer { get; set; } = "";
        public string Database { get; set; } = "";
        public bool TrustServerCertificate { get; set; }
    }

    public sealed class InstallActiveDirectory
    {
        public SourceMode Mode { get; set; } = SourceMode.Disabled;
        public string Domain { get; set; } = "";
        public string? Server { get; set; }
        public List<string> SearchBases { get; set; } = [];
        public bool UseLdaps { get; set; }
    }

    /// <summary>Applies the answers over the current settings, keeping what the answers do not cover.</summary>
    public NexusSettings ApplyTo(NexusSettings current)
    {
        current.Database.Provider = Database.Provider;
        current.Database.ConnectionString = Database.ConnectionString;

        current.Sccm.Mode = DemoMode ? SourceMode.Simulated : Sccm.Mode;
        current.Sccm.SiteCode = Sccm.SiteCode;
        current.Sccm.SqlServer = Sccm.SqlServer;
        current.Sccm.Database = Sccm.Database;
        current.Sccm.TrustServerCertificate = Sccm.TrustServerCertificate;

        if (DemoMode)
        {
            current.Xdr.Mode = SourceMode.Simulated;
            current.Netskope.Mode = SourceMode.Simulated;
        }

        current.ActiveDirectory.Mode = DemoMode ? SourceMode.Simulated : ActiveDirectory.Mode;
        current.ActiveDirectory.Domain = DemoMode && ActiveDirectory.Domain.Length == 0 ? Nexus.Simulation.SyntheticEstate.Domain : ActiveDirectory.Domain;
        current.ActiveDirectory.Server = ActiveDirectory.Server;
        current.ActiveDirectory.SearchBases = ActiveDirectory.SearchBases;
        current.ActiveDirectory.UseLdaps = ActiveDirectory.UseLdaps;

        if (Collection is not null)
        {
            current.Collection = Collection;
        }

        if (!string.IsNullOrWhiteSpace(PublicUrl))
        {
            current.Web.PublicUrl = PublicUrl;
        }

        if (Hosting is "service" or "iis")
        {
            current.Web.Hosting = Hosting;
        }

        if (HttpsPort is > 0)
        {
            current.Web.HttpsPort = HttpsPort.Value;
        }

        if (!string.IsNullOrWhiteSpace(CertificateThumbprint))
        {
            current.Web.CertificateThumbprint = CertificateThumbprint;
        }

        current.DemoMode = DemoMode;
        return current;
    }
}
