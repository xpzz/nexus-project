using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Nexus.Core.Configuration;

namespace Nexus.Collectors.Xdr;

/// <summary>Cortex XDR endpoint as stored by the site's routine (get_endpoints). Never carries the API key.</summary>
public sealed record XdrEndpoint(
    string AgentId,
    string? HostName,
    string? AgentStatus,
    string? OperationalStatus,
    string? AgentType,
    string? Ip,
    DateTimeOffset? LastSeen,
    string? Users);

public interface IXdrReader
{
    IAsyncEnumerable<XdrEndpoint> ReadEndpointsAsync(CancellationToken cancellationToken);
}

/// <summary>Identifiers are validated, never concatenated from free text: the table name comes from configuration.</summary>
public static partial class XdrIdentifiers
{
    public static string QuotedTable(string table)
    {
        var parts = table.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(p => !Part().IsMatch(p)))
        {
            throw new ArgumentException($"Nome de tabela inválido: '{table}'. Use letras, números e sublinhado, opcionalmente 'esquema.tabela'.", nameof(table));
        }

        return parts.Length == 1 ? $"[dbo].[{parts[0]}]" : $"[{parts[0]}].[{parts[1]}]";
    }

    public static string Database(string database)
    {
        if (!Part().IsMatch(database))
        {
            throw new ArgumentException($"Nome de banco inválido: '{database}'.", nameof(database));
        }

        return database;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex Part();
}

public static class XdrConnectionFactory
{
    public static string BuildConnectionString(XdrSettings settings) => new SqlConnectionStringBuilder
    {
        DataSource = settings.SqlServer,
        InitialCatalog = XdrIdentifiers.Database(settings.Database),
        IntegratedSecurity = true,
        Encrypt = SqlConnectionEncryptOption.Mandatory,
        TrustServerCertificate = settings.TrustServerCertificate,
        ApplicationIntent = ApplicationIntent.ReadOnly,
        ApplicationName = "Azul Nexus",
        ConnectTimeout = 15,
        CommandTimeout = settings.CommandTimeoutSeconds,
    }.ConnectionString;
}

/// <summary>
/// Reads the table filled by the XDR routine. <c>last_seen</c> is stored as the UTC clock time of the Unix epoch value
/// (the routine converts with DateTimeOffset.FromUnixTimeMilliseconds(...).DateTime), so it is read as UTC.
/// </summary>
public sealed class SqlXdrReader(XdrSettings settings) : IXdrReader
{
    private const string Isolation = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n";

    public static string BuildQuery(XdrSettings settings) => Isolation +
        $"SELECT agent_id, host_name, Agent_status, operational_status, agent_type, ip, last_seen, users FROM {XdrIdentifiers.QuotedTable(settings.Table)};";

    public async IAsyncEnumerable<XdrEndpoint> ReadEndpointsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(XdrConnectionFactory.BuildConnectionString(settings));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(BuildQuery(settings), connection) { CommandTimeout = settings.CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            yield return new XdrEndpoint(
                Convert.ToString(reader.GetValue(0))!,
                Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
                reader.IsDBNull(6) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
                Text(reader, 7));
        }
    }

    private static string? Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
}
