using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Nexus.Core.Configuration;

namespace Nexus.Collectors.Sccm;

public sealed record SccmSystem(
    int ResourceId,
    string? Name,
    string? Domain,
    bool? Client,
    bool? Active,
    bool? Obsolete,
    Guid? AadDeviceId,
    string? SmbiosGuid,
    string? OperatingSystem,
    string? Serial = null,
    string? Manufacturer = null,
    string? Model = null,
    DateTimeOffset? LastActiveAt = null,
    int? ClientActiveStatus = null);

public interface ISccmReader
{
    IAsyncEnumerable<SccmSystem> ReadSystemsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads v_R_System in keyset batches by ResourceID, only the needed columns, with
/// READ UNCOMMITTED so the site writes are never blocked (trade-off recorded in ADR-0003).
/// </summary>
public sealed class SqlSccmReader(SccmSettings settings, SccmQueryGate gate) : ISccmReader
{
    private const string Isolation = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n";

    /// <summary>Base query: v_R_System only (the columns Phase 0 already reads in production).</summary>
    public const string BaseQuery = Isolation + """
        SELECT TOP (@batch)
            ResourceID, Name0, Resource_Domain_OR_Workgr0, Client0, Active0, Obsolete0,
            AADDeviceID, SMBIOS_GUID0, Operating_System_Name_and0,
            CAST(NULL AS nvarchar(128)) AS SerialNumber0, CAST(NULL AS nvarchar(128)) AS Manufacturer0, CAST(NULL AS nvarchar(128)) AS Model0,
            CAST(NULL AS datetime) AS LastActiveTime, CAST(NULL AS int) AS ClientActiveStatus
        FROM dbo.v_R_System
        WHERE ResourceID > @last
        ORDER BY ResourceID;
        """;

    /// <summary>Adds serial, manufacturer, model and last client activity (SPEC §6.1) with LEFT JOINs on the same ResourceID.</summary>
    public const string ExtendedQuery = Isolation + """
        SELECT TOP (@batch)
            s.ResourceID, s.Name0, s.Resource_Domain_OR_Workgr0, s.Client0, s.Active0, s.Obsolete0,
            s.AADDeviceID, s.SMBIOS_GUID0, s.Operating_System_Name_and0,
            b.SerialNumber0, cs.Manufacturer0, cs.Model0, c.LastActiveTime, c.ClientActiveStatus
        FROM dbo.v_R_System s
        LEFT JOIN dbo.v_GS_PC_BIOS b ON b.ResourceID = s.ResourceID
        LEFT JOIN dbo.v_GS_COMPUTER_SYSTEM cs ON cs.ResourceID = s.ResourceID
        LEFT JOIN dbo.v_CH_ClientSummary c ON c.ResourceID = s.ResourceID
        WHERE s.ResourceID > @last
        ORDER BY s.ResourceID;
        """;

    // SQL errors that mean "this site does not have that view/column or the account cannot read it".
    private static readonly int[] FallbackErrors = [207, 208, 229];

    public bool UsedExtendedQuery { get; private set; }

    public string? FallbackReason { get; private set; }

    public async IAsyncEnumerable<SccmSystem> ReadSystemsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var connectionString = SccmConnectionFactory.BuildConnectionString(settings);
        var last = int.MinValue;
        var query = ExtendedQuery;
        UsedExtendedQuery = true;
        while (true)
        {
            List<SccmSystem> batch;
            try
            {
                batch = await gate.RunAsync(ct => ReadBatchAsync(connectionString, query, last, ct), cancellationToken);
            }
            catch (SqlException ex) when (query == ExtendedQuery && last == int.MinValue && FallbackErrors.Contains(ex.Number))
            {
                // The base inventory keeps working; serial/model/last activity are reported as unavailable.
                query = BaseQuery;
                UsedExtendedQuery = false;
                FallbackReason = $"Consulta estendida indisponível neste site ({ex.Message}); coletando só v_R_System.";
                continue;
            }

            foreach (var system in batch)
            {
                yield return system;
            }

            if (batch.Count < settings.BatchSize)
            {
                yield break;
            }

            last = batch[^1].ResourceId;
        }
    }

    private async Task<List<SccmSystem>> ReadBatchAsync(string connectionString, string query, int last, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection) { CommandTimeout = settings.CommandTimeoutSeconds };
        command.Parameters.AddWithValue("@batch", settings.BatchSize);
        command.Parameters.AddWithValue("@last", last);

        var result = new List<SccmSystem>(settings.BatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SccmSystem(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ReadFlag(reader, 3),
                ReadFlag(reader, 4),
                ReadFlag(reader, 5),
                reader.IsDBNull(6) ? null : ReadGuid(reader.GetValue(6)),
                reader.IsDBNull(7) ? null : Convert.ToString(reader.GetValue(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : Convert.ToString(reader.GetValue(9)),
                reader.IsDBNull(10) ? null : Convert.ToString(reader.GetValue(10)),
                reader.IsDBNull(11) ? null : Convert.ToString(reader.GetValue(11)),
                reader.IsDBNull(12) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc)),
                reader.IsDBNull(13) ? null : Convert.ToInt32(reader.GetValue(13))));
        }

        return result;
    }

    private static bool? ReadFlag(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal)) != 0;

    private static Guid? ReadGuid(object value) => value switch
    {
        Guid guid => guid,
        string text when Guid.TryParse(text, out var parsed) => parsed,
        _ => null,
    };
}
