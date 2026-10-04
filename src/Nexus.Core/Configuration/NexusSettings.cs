using System.Text.Json.Serialization;

namespace Nexus.Core.Configuration;

/// <summary>Configuration persisted in config/nexus.json. Never holds secrets in clear text.</summary>
public sealed class NexusSettings
{
    public int SchemaVersion { get; set; } = 1;
    public DatabaseSettings Database { get; set; } = new();
    public SccmSettings Sccm { get; set; } = new();
    public ActiveDirectorySettings ActiveDirectory { get; set; } = new();
    public XdrSettings Xdr { get; set; } = new();
    public CollectionSettings Collection { get; set; } = new();
    public WebSettings Web { get; set; } = new();
    public bool DemoMode { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<DatabaseProvider>))]
public enum DatabaseProvider
{
    SqlServer,
    PostgreSql,
}

public sealed class DatabaseSettings
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;

    /// <summary>Connection string without password. SQL Server uses integrated security.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>PostgreSQL password protected with DPAPI (machine scope). Null for integrated security.</summary>
    public string? ProtectedPassword { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceMode>))]
public enum SourceMode
{
    /// <summary>Source not configured: shown as "não configurado", never as zero.</summary>
    Disabled,
    /// <summary>Real source.</summary>
    Live,
    /// <summary>Synthetic data (simulators and demo mode).</summary>
    Simulated,
}

public sealed class SccmSettings
{
    public SourceMode Mode { get; set; } = SourceMode.Disabled;
    public string SiteCode { get; set; } = "";
    public string SqlServer { get; set; } = "";
    public string Database { get; set; } = "";
    public bool TrustServerCertificate { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 120;
    public int MaxConcurrentQueries { get; set; } = 2;
    public int BatchSize { get; set; } = 5000;
    /// <summary>Query slower than this postpones the next collection (backoff).</summary>
    public int SlowQueryThresholdSeconds { get; set; } = 60;
}

/// <summary>
/// Cortex XDR endpoints, read from the SQL table that the site's own PowerShell routine fills from the XDR API.
/// The Nexus never holds the XDR API key: it only reads that table (read-only intent, integrated security).
/// </summary>
public sealed class XdrSettings
{
    public SourceMode Mode { get; set; } = SourceMode.Disabled;
    public string SqlServer { get; set; } = "";
    public string Database { get; set; } = "cortex_db";
    /// <summary>Table (optionally "schema.table"; schema defaults to dbo).</summary>
    public string Table { get; set; } = "API_Cortex_getAllEndpoints";
    public bool TrustServerCertificate { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 60;
}

public sealed class ActiveDirectorySettings
{
    public SourceMode Mode { get; set; } = SourceMode.Disabled;
    public string Domain { get; set; } = "";
    public string? Server { get; set; }
    public List<string> SearchBases { get; set; } = [];
    public bool UseLdaps { get; set; }
    public int PageSize { get; set; } = 500;
}

public sealed class CollectionSettings
{
    public int SccmIntervalMinutes { get; set; } = 30;
    public int ActiveDirectoryIntervalMinutes { get; set; } = 240;
    /// <summary>Intune and Entra ID (SPEC §10: 30 to 60 minutes for Intune).</summary>
    public int GraphIntervalMinutes { get; set; } = 60;
    public int ReconcileIntervalMinutes { get; set; } = 15;
    /// <summary>Policy catalog and per-device policy states (heavy: one batched call per 10 devices).</summary>
    public int PoliciesIntervalMinutes { get; set; } = 240;
    public int MamIntervalMinutes { get; set; } = 120;
    public int UsersIntervalMinutes { get; set; } = 240;
    public int XdrIntervalMinutes { get; set; } = 60;
    public int MaxCpuPercent { get; set; } = 25;
    public int MaxMemoryMegabytes { get; set; } = 1024;
    /// <summary>Server CPU above this value postpones collections.</summary>
    public int ServerCpuBackoffPercent { get; set; } = 85;
    public List<PauseWindow> PauseWindows { get; set; } = [];
    public bool Paused { get; set; }
}

public sealed class WebSettings
{
    /// <summary>Public base URL, e.g. https://nexus.azul.local:8443.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>
    /// "service": the site runs as a Windows service with Kestrel (needs only "log on as a service"; no IIS).
    /// "iis": the site runs in an IIS application pool (the pool identity needs "log on as a batch job").
    /// </summary>
    public string Hosting { get; set; } = "service";

    /// <summary>HTTPS port used in "service" hosting (in "iis" hosting the binding comes from IIS).</summary>
    public int HttpsPort { get; set; } = 8443;

    /// <summary>Thumbprint of the server certificate in LocalMachine\My, used in "service" hosting.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// While there is no SSO, the inventory screens are open to anyone who can reach the site (read-only).
    /// Configuration, health actions and diagnostics always need the server itself (localhost) or the one-time setup code.
    /// Set to false to also close the inventory screens behind setup mode.
    /// </summary>
    public bool OpenAccess { get; set; } = true;
}
