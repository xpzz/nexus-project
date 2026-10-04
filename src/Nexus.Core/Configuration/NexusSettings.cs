using System.Text.Json.Serialization;

namespace Nexus.Core.Configuration;

/// <summary>Configuration persisted in config/nexus.json. Never holds secrets in clear text.</summary>
public sealed class NexusSettings
{
    public EvidenceSettings Evidence { get; set; } = new();

    public int SchemaVersion { get; set; } = 1;
    public DatabaseSettings Database { get; set; } = new();
    public SccmSettings Sccm { get; set; } = new();
    public ActiveDirectorySettings ActiveDirectory { get; set; } = new();
    public XdrSettings Xdr { get; set; } = new();
    public NetskopeSettings Netskope { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
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

/// <summary>Outbound proxy for the calls to Microsoft Graph and Netskope (the server may only reach the internet through the corporate proxy).</summary>
public sealed class NetworkSettings
{
    /// <summary>Empty = direct connection. Example: http://proxy.azul.corp:8080.</summary>
    public string ProxyUrl { get; set; } = "";

    /// <summary>Authenticate to the proxy with the service account (Windows integrated).</summary>
    public bool ProxyUseDefaultCredentials { get; set; } = true;
}

/// <summary>
/// Netskope client (agent) data from the tenant REST API. The API token is stored protected (DPAPI, machine scope),
/// never in clear text; <c>NEXUS_NETSKOPE_TOKEN</c> overrides it for development.
/// </summary>
public sealed class NetskopeSettings
{
    public SourceMode Mode { get; set; } = SourceMode.Disabled;

    /// <summary>Tenant host, e.g. azul.goskope.com (without https://).</summary>
    public string Tenant { get; set; } = "";

    /// <summary>Endpoint that returns the client data ("Get Client Data"). Configurable because the API versions differ.</summary>
    public string ClientsPath { get; set; } = "/api/v1/clients";

    /// <summary>"header" sends Netskope-Api-Token (REST API v2); "query" sends token=... (REST API v1).</summary>
    public string TokenPlacement { get; set; } = "query";

    /// <summary>Paging parameters: page size and the name of the offset parameter ("skip" or "offset").</summary>
    public int PageSize { get; set; } = 500;
    public string OffsetParameter { get; set; } = "skip";

    public string? ProtectedToken { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
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

/// <summary>How recent a report must be, in days. Each step must be larger than the previous one.</summary>
public sealed class EvidenceThresholds
{
    /// <summary>Two independent tools reported inside this window: confirmed active.</summary>
    public int ConfirmedDays { get; set; } = 7;
    /// <summary>One tool that runs on the device (or two identity sources) reported inside this window: probably active.</summary>
    public int ProbableDays { get; set; } = 30;
    /// <summary>Some report exists inside this window, but nothing recent: no recent evidence.</summary>
    public int NoRecentDays { get; set; } = 90;
    /// <summary>Nothing for this long: candidate to be retired.</summary>
    public int DecommissionDays { get; set; } = 180;

    public EvidenceThresholds() { }

    public EvidenceThresholds(int confirmed, int probable, int noRecent, int decommission)
    {
        (ConfirmedDays, ProbableDays, NoRecentDays, DecommissionDays) = (confirmed, probable, noRecent, decommission);
    }

    /// <summary>Keeps the steps ordered so a typo in nexus.json cannot invert the classification.</summary>
    public EvidenceThresholds Normalized()
    {
        var confirmed = Math.Clamp(ConfirmedDays, 1, 365);
        var probable = Math.Clamp(ProbableDays, confirmed, 730);
        var noRecent = Math.Clamp(NoRecentDays, probable, 1095);
        return new EvidenceThresholds(confirmed, probable, noRecent, Math.Clamp(DecommissionDays, noRecent, 3650));
    }
}

/// <summary>Configurable activity evidence engine (ADR-0007): thresholds per asset type, how much each tool is trusted and how types are recognized.</summary>
public sealed class EvidenceSettings
{
    public EvidenceThresholds Default { get; set; } = new();

    /// <summary>Overrides by asset type (desktop, notebook, server, phone, tablet, mac, shared, kiosk, iot).</summary>
    public Dictionary<string, EvidenceThresholds> ByType { get; set; } = new()
    {
        ["phone"] = new(14, 45, 90, 180),
        ["tablet"] = new(14, 45, 90, 180),
        ["shared"] = new(14, 60, 120, 240),
        ["kiosk"] = new(14, 60, 120, 240),
        ["iot"] = new(14, 60, 120, 240),
    };

    /// <summary>Probability (0 to 1) that a fresh report from the tool means the device is in use. Identity sources weigh less than telemetry.</summary>
    public Dictionary<string, double> Reliability { get; set; } = new()
    {
        ["sccm"] = 0.75, ["intune"] = 0.80, ["xdr"] = 0.80, ["netskope"] = 0.70, ["mam"] = 0.55, ["entra"] = 0.40, ["ad"] = 0.25,
    };

    /// <summary>Independent tools needed inside the confirmed window to call a device confirmed.</summary>
    public int MinConfirmedSources { get; set; } = 2;

    /// <summary>Name tokens (separated by - _ . or digits) that identify a type when the hardware says nothing. Empty by default except where the convention is universal.</summary>
    public Dictionary<string, List<string>> TypeNameTokens { get; set; } = new()
    {
        ["notebook"] = ["NB", "NOTE", "NTB", "LT", "LAP"],
        ["desktop"] = ["DT", "DSK", "DESK"],
        ["kiosk"] = ["KIOSK", "KSK"],
        ["shared"] = ["SHARED", "COMPART"],
        ["iot"] = ["IOT"],
    };
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
    public int NetskopeIntervalMinutes { get; set; } = 60;
    /// <summary>How long superseded versions of source records, evidence dates and asset changes are kept (minimum 30).</summary>
    public int HistoryRetentionDays { get; set; } = 400;

    /// <summary>Legacy. The thresholds that decide whether a device is active now live in <see cref="NexusSettings.Evidence"/> (ADR-0007).</summary>
    public int ActivityWindowDays { get; set; } = 30;
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

    /// <summary>
    /// "open": screens are open for reading and operator pages need the server or the setup code (default, ADR-0005).
    /// "entra": every visitor signs in with Microsoft Entra ID and gets the app roles of the "Azul Nexus – Web" registration (ADR-0008).
    /// </summary>
    public string AuthMode { get; set; } = "open";

    public bool UsesEntra => string.Equals(AuthMode, "entra", StringComparison.OrdinalIgnoreCase);
}
