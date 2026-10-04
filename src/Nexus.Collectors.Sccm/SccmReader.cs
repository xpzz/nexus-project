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
    int? ClientActiveStatus = null,
    string? ClientVersion = null,
    DateTimeOffset? LastPolicyRequestAt = null,
    DateTimeOffset? LastHwScanAt = null,
    DateTimeOffset? LastSwScanAt = null,
    DateTimeOffset? LastDdrAt = null,
    string? LastLogonUser = null,
    string? AdSite = null,
    string? OsVersion = null,
    DateTimeOffset? LastBootAt = null,
    string? CpuName = null,
    int? CpuCores = null,
    long? MemoryMb = null,
    long? DiskTotalMb = null,
    long? DiskFreeMb = null,
    string? BiosVersion = null);

public sealed record SccmSoftware(string Name, string? Version, string? Publisher, DateTimeOffset? InstalledOn);

public interface ISccmReader
{
    IAsyncEnumerable<SccmSystem> ReadSystemsAsync(CancellationToken cancellationToken);

    /// <summary>Installed programs of one device (Add/Remove Programs, 32 and 64 bit), read on demand.</summary>
    Task<IReadOnlyList<SccmSoftware>> ReadSoftwareAsync(int resourceId, CancellationToken cancellationToken);
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

    /// <summary>
    /// Adds client version, last policy request, last hardware and software inventory, last heartbeat, last logged-on user,
    /// operating system version and boot time, CPU, memory, system disk and BIOS (SPEC v1 §8.3). Every join is on ResourceID.
    /// </summary>
    public const string HardwareQuery = Isolation + """
        SELECT TOP (@batch)
            s.ResourceID, s.Name0, s.Resource_Domain_OR_Workgr0, s.Client0, s.Active0, s.Obsolete0,
            s.AADDeviceID, s.SMBIOS_GUID0, s.Operating_System_Name_and0,
            b.SerialNumber0, cs.Manufacturer0, cs.Model0, c.LastActiveTime, c.ClientActiveStatus,
            s.Client_Version0, c.LastPolicyRequest, c.LastHW, c.LastSW, c.LastDDR, cs.UserName0, s.AD_Site_Name0,
            os.Version0, os.LastBootUpTime0, cpu.Name0, cpu.NumberOfCores0, mem.TotalPhysicalMemory0, ld.Size0, ld.FreeSpace0, b.SMBIOSBIOSVersion0
        FROM dbo.v_R_System s
        LEFT JOIN dbo.v_GS_PC_BIOS b ON b.ResourceID = s.ResourceID
        LEFT JOIN dbo.v_GS_COMPUTER_SYSTEM cs ON cs.ResourceID = s.ResourceID
        LEFT JOIN dbo.v_CH_ClientSummary c ON c.ResourceID = s.ResourceID
        LEFT JOIN dbo.v_GS_OPERATING_SYSTEM os ON os.ResourceID = s.ResourceID
        LEFT JOIN dbo.v_GS_X86_PC_MEMORY mem ON mem.ResourceID = s.ResourceID
        LEFT JOIN dbo.v_GS_LOGICAL_DISK ld ON ld.ResourceID = s.ResourceID AND ld.DeviceID0 = 'C:'
        OUTER APPLY (SELECT TOP (1) p.Name0, p.NumberOfCores0 FROM dbo.v_GS_PROCESSOR p WHERE p.ResourceID = s.ResourceID) cpu
        WHERE s.ResourceID > @last
        ORDER BY s.ResourceID;
        """;

    public const string SoftwareQuery = Isolation + """
        SELECT DisplayName0, Version0, Publisher0, InstallDate0 FROM dbo.v_GS_ADD_REMOVE_PROGRAMS WHERE ResourceID = @id AND DisplayName0 IS NOT NULL
        UNION ALL
        SELECT DisplayName0, Version0, Publisher0, InstallDate0 FROM dbo.v_GS_ADD_REMOVE_PROGRAMS_64 WHERE ResourceID = @id AND DisplayName0 IS NOT NULL
        ORDER BY 1;
        """;

    // SQL errors that mean "this site does not have that view/column or the account cannot read it".
    private static readonly int[] FallbackErrors = [207, 208, 229];

    /// <summary>0 = hardware and client details, 1 = serial, model and last activity, 2 = v_R_System only.</summary>
    public int Tier { get; private set; }

    public bool UsedExtendedQuery => Tier <= 1;

    public string? FallbackReason { get; private set; }

    private static readonly string[] Tiers = [HardwareQuery, ExtendedQuery, BaseQuery];

    public async IAsyncEnumerable<SccmSystem> ReadSystemsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var connectionString = SccmConnectionFactory.BuildConnectionString(settings);
        var last = int.MinValue;
        Tier = 0;
        while (true)
        {
            List<SccmSystem> batch;
            try
            {
                var query = Tiers[Tier];
                batch = await gate.RunAsync(ct => ReadBatchAsync(connectionString, query, last, ct), cancellationToken);
            }
            catch (SqlException ex) when (Tier < Tiers.Length - 1 && last == int.MinValue && FallbackErrors.Contains(ex.Number))
            {
                // The base inventory keeps working; the details the site cannot give are reported as unavailable.
                Tier++;
                FallbackReason = Tier == 1
                    ? $"Consulta de hardware indisponível neste site ({ex.Message}); coletando sem CPU, memória, disco e datas do cliente. Rode 'nexusctl sccm-grant' para liberar as views novas."
                    : $"Consulta estendida indisponível neste site ({ex.Message}); coletando só v_R_System.";
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
                reader.IsDBNull(13) ? null : Convert.ToInt32(reader.GetValue(13)),
                Str(reader, 14), Dt(reader, 15), Dt(reader, 16), Dt(reader, 17), Dt(reader, 18), Str(reader, 19), Str(reader, 20),
                Str(reader, 21), Dt(reader, 22), Str(reader, 23), Num(reader, 24) is { } cores ? (int)cores : null,
                Num(reader, 25) is { } kb ? kb / 1024 : null, Num(reader, 26), Num(reader, 27), Str(reader, 28)));
        }

        return result;
    }

    public async Task<IReadOnlyList<SccmSoftware>> ReadSoftwareAsync(int resourceId, CancellationToken cancellationToken)
    {
        var connectionString = SccmConnectionFactory.BuildConnectionString(settings);
        return await gate.RunAsync(async ct =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(SoftwareQuery, connection) { CommandTimeout = settings.CommandTimeoutSeconds };
            command.Parameters.AddWithValue("@id", resourceId);
            var list = new List<SccmSoftware>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var raw = reader.IsDBNull(3) ? null : Convert.ToString(reader.GetValue(3));
                DateTimeOffset? installed = raw is { Length: 8 } && DateTime.TryParseExact(raw, "yyyyMMdd", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? new DateTimeOffset(d, TimeSpan.Zero) : null;
                list.Add(new SccmSoftware(reader.GetString(0), Str(reader, 1), Str(reader, 2), installed));
            }

            return (IReadOnlyList<SccmSoftware>)list;
        }, cancellationToken);
    }

    // The base and extended queries have fewer columns: anything past the end is "not collected".
    private static string? Str(SqlDataReader r, int i) => i >= r.FieldCount || r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));

    private static long? Num(SqlDataReader r, int i) => i >= r.FieldCount || r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));

    private static DateTimeOffset? Dt(SqlDataReader r, int i) =>
        i >= r.FieldCount || r.IsDBNull(i) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc));

    private static bool? ReadFlag(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal)) != 0;

    private static Guid? ReadGuid(object value) => value switch
    {
        Guid guid => guid,
        string text when Guid.TryParse(text, out var parsed) => parsed,
        _ => null,
    };
}
