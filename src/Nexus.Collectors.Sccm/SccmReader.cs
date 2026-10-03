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
    string? OperatingSystem);

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
    private const string Query = """
        SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
        SELECT TOP (@batch)
            ResourceID, Name0, Resource_Domain_OR_Workgr0, Client0, Active0, Obsolete0,
            AADDeviceID, SMBIOS_GUID0, Operating_System_Name_and0
        FROM dbo.v_R_System
        WHERE ResourceID > @last
        ORDER BY ResourceID;
        """;

    public async IAsyncEnumerable<SccmSystem> ReadSystemsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var connectionString = SccmConnectionFactory.BuildConnectionString(settings);
        var last = int.MinValue;
        while (true)
        {
            var batch = await gate.RunAsync(ct => ReadBatchAsync(connectionString, last, ct), cancellationToken);
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

    private async Task<List<SccmSystem>> ReadBatchAsync(string connectionString, int last, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(Query, connection) { CommandTimeout = settings.CommandTimeoutSeconds };
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
                reader.IsDBNull(8) ? null : reader.GetString(8)));
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
