using Microsoft.Data.SqlClient;
using Nexus.Core.Configuration;

namespace Nexus.Collectors.Sccm;

public static class SccmConnectionFactory
{
    /// <summary>
    /// Integrated security with the Worker identity, read-only intent and encryption on by default.
    /// </summary>
    public static string BuildConnectionString(SccmSettings settings) => new SqlConnectionStringBuilder
    {
        DataSource = settings.SqlServer,
        InitialCatalog = settings.Database,
        IntegratedSecurity = true,
        Encrypt = SqlConnectionEncryptOption.Mandatory,
        TrustServerCertificate = settings.TrustServerCertificate,
        ApplicationIntent = ApplicationIntent.ReadOnly,
        ApplicationName = "Azul Nexus",
        ConnectTimeout = 15,
        CommandTimeout = settings.CommandTimeoutSeconds,
    }.ConnectionString;
}
