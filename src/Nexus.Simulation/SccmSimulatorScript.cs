using System.Globalization;
using System.Text;

namespace Nexus.Simulation;

/// <summary>
/// T-SQL that creates a simulated site database (tables behind views with the real names and
/// columns read by the Nexus) in a test SQL Server, e.g. a container. Never run against a real site.
/// </summary>
public static class SccmSimulatorScript
{
    public static string Create(SyntheticEstate estate, string database = "CM_SIM")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}];");
        sb.AppendLine("GO");
        sb.AppendLine($"USE [{database}];");
        sb.AppendLine("GO");
        sb.AppendLine("""
            IF OBJECT_ID(N'dbo.sim_System') IS NULL
            CREATE TABLE dbo.sim_System (
                ResourceID int NOT NULL PRIMARY KEY,
                Name0 nvarchar(256) NULL,
                Resource_Domain_OR_Workgr0 nvarchar(256) NULL,
                Client0 int NULL,
                Active0 int NULL,
                Obsolete0 int NULL,
                AADDeviceID uniqueidentifier NULL,
                SMBIOS_GUID0 nvarchar(64) NULL,
                Operating_System_Name_and0 nvarchar(256) NULL,
                SerialNumber0 nvarchar(128) NULL,
                Manufacturer0 nvarchar(128) NULL,
                Model0 nvarchar(128) NULL,
                LastActiveTime datetime NULL,
                ClientActiveStatus int NULL,
                Client_Version0 nvarchar(32) NULL,
                LastPolicyRequest datetime NULL,
                LastHW datetime NULL,
                LastSW datetime NULL,
                LastDDR datetime NULL,
                UserName0 nvarchar(256) NULL,
                AD_Site_Name0 nvarchar(128) NULL,
                OsVersion0 nvarchar(64) NULL,
                LastBootUpTime0 datetime NULL,
                CpuName0 nvarchar(256) NULL,
                CpuCores0 int NULL,
                MemoryKb bigint NULL,
                DiskSizeMb bigint NULL,
                DiskFreeMb bigint NULL,
                BiosVersion0 nvarchar(128) NULL);
            GO
            CREATE OR ALTER VIEW dbo.v_R_System AS
                SELECT ResourceID, Name0, Resource_Domain_OR_Workgr0, Client0, Active0, Obsolete0,
                       AADDeviceID, SMBIOS_GUID0, Operating_System_Name_and0, Client_Version0, AD_Site_Name0
                FROM dbo.sim_System;
            GO
            CREATE OR ALTER VIEW dbo.v_GS_PC_BIOS AS
                SELECT ResourceID, SerialNumber0, BiosVersion0 AS SMBIOSBIOSVersion0 FROM dbo.sim_System;
            GO
            CREATE OR ALTER VIEW dbo.v_GS_COMPUTER_SYSTEM AS
                SELECT ResourceID, Manufacturer0, Model0, UserName0 FROM dbo.sim_System;
            GO
            CREATE OR ALTER VIEW dbo.v_CH_ClientSummary AS
                SELECT ResourceID, ClientActiveStatus, LastActiveTime, LastPolicyRequest, LastHW, LastSW, LastDDR FROM dbo.sim_System WHERE ClientActiveStatus IS NOT NULL;
            GO
            CREATE OR ALTER VIEW dbo.v_GS_OPERATING_SYSTEM AS
                SELECT ResourceID, OsVersion0 AS Version0, LastBootUpTime0 FROM dbo.sim_System;
            GO
            CREATE OR ALTER VIEW dbo.v_GS_PROCESSOR AS
                SELECT ResourceID, CpuName0 AS Name0, CpuCores0 AS NumberOfCores0 FROM dbo.sim_System WHERE CpuName0 IS NOT NULL;
            GO
            CREATE OR ALTER VIEW dbo.v_GS_X86_PC_MEMORY AS
                SELECT ResourceID, MemoryKb AS TotalPhysicalMemory0 FROM dbo.sim_System WHERE MemoryKb IS NOT NULL;
            GO
            CREATE OR ALTER VIEW dbo.v_GS_LOGICAL_DISK AS
                SELECT ResourceID, N'C:' AS DeviceID0, DiskSizeMb AS Size0, DiskFreeMb AS FreeSpace0 FROM dbo.sim_System WHERE DiskSizeMb IS NOT NULL;
            GO
            IF OBJECT_ID(N'dbo.sim_Software') IS NULL
            CREATE TABLE dbo.sim_Software (ResourceID int NOT NULL, DisplayName0 nvarchar(512) NULL, Version0 nvarchar(128) NULL, Publisher0 nvarchar(256) NULL, InstallDate0 nvarchar(32) NULL);
            GO
            CREATE OR ALTER VIEW dbo.v_GS_ADD_REMOVE_PROGRAMS AS
                SELECT ResourceID, DisplayName0, Version0, Publisher0, InstallDate0 FROM dbo.sim_Software;
            GO
            CREATE OR ALTER VIEW dbo.v_GS_ADD_REMOVE_PROGRAMS_64 AS
                SELECT ResourceID, DisplayName0, Version0, Publisher0, InstallDate0 FROM dbo.sim_Software WHERE 1 = 0;
            GO
            """);

        // Remaining views exist with their key column so access checks and grants behave as in a site.
        foreach (var view in Nexus.Collectors.Sccm.SccmViews.All.Except(["v_R_System", "v_GS_PC_BIOS", "v_GS_COMPUTER_SYSTEM", "v_CH_ClientSummary", "v_GS_OPERATING_SYSTEM", "v_GS_PROCESSOR", "v_GS_X86_PC_MEMORY", "v_GS_LOGICAL_DISK", "v_GS_ADD_REMOVE_PROGRAMS", "v_GS_ADD_REMOVE_PROGRAMS_64"]))
        {
            sb.AppendLine($"CREATE OR ALTER VIEW dbo.{view} AS SELECT ResourceID FROM dbo.sim_System WHERE 1 = 0;");
            sb.AppendLine("GO");
        }

        sb.AppendLine("DELETE FROM dbo.sim_System;");
        sb.AppendLine("DELETE FROM dbo.sim_Software;");
        foreach (var s in estate.SccmSystems)
        {
            var serial = s.Serial;
            sb.Append("INSERT INTO dbo.sim_System VALUES (")
                .Append(s.ResourceId.ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(Text(s.Name)).Append(", ")
                .Append(Text(s.Domain)).Append(", ")
                .Append(Flag(s.Client)).Append(", ")
                .Append(Flag(s.Active)).Append(", ")
                .Append(Flag(s.Obsolete)).Append(", ")
                .Append(s.AadDeviceId is { } id ? $"'{id}'" : "NULL").Append(", ")
                .Append(Text(s.SmbiosGuid)).Append(", ")
                .Append(Text(s.OperatingSystem)).Append(", ")
                .Append(Text(serial)).Append(", ")
                .Append(Text(s.Manufacturer)).Append(", ")
                .Append(Text(s.Model)).Append(", ")
                .Append(s.LastActiveAt is { } at ? $"'{at.UtcDateTime:yyyy-MM-dd HH:mm:ss}'" : "NULL").Append(", ")
                .Append(s.ClientActiveStatus is { } cas ? cas.ToString(CultureInfo.InvariantCulture) : "NULL").Append(", ")
                .Append(Text(s.ClientVersion)).Append(", ")
                .Append(Date(s.LastPolicyRequestAt)).Append(", ").Append(Date(s.LastHwScanAt)).Append(", ").Append(Date(s.LastSwScanAt)).Append(", ").Append(Date(s.LastDdrAt)).Append(", ")
                .Append(Text(s.LastLogonUser)).Append(", ").Append(Text(s.AdSite)).Append(", ").Append(Text(s.OsVersion)).Append(", ").Append(Date(s.LastBootAt)).Append(", ")
                .Append(Text(s.CpuName)).Append(", ").Append(Number(s.CpuCores)).Append(", ").Append(Number(s.MemoryMb * 1024)).Append(", ")
                .Append(Number(s.DiskTotalMb)).Append(", ").Append(Number(s.DiskFreeMb)).Append(", ").Append(Text(s.BiosVersion)).AppendLine(");");
        }

        sb.AppendLine("GO");
        return sb.ToString();
    }

    private static string Date(DateTimeOffset? value) => value is { } at ? $"'{at.UtcDateTime:yyyy-MM-dd HH:mm:ss}'" : "NULL";

    private static string Number(long? value) => value is { } n ? n.ToString(CultureInfo.InvariantCulture) : "NULL";

    private static string Text(string? value) => value is null ? "NULL" : $"N'{value.Replace("'", "''")}'";

    private static string Flag(bool? value) => value is null ? "NULL" : value.Value ? "1" : "0";
}
