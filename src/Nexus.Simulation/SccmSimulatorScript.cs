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
                SerialNumber0 nvarchar(64) NULL);
            GO
            CREATE OR ALTER VIEW dbo.v_R_System AS
                SELECT ResourceID, Name0, Resource_Domain_OR_Workgr0, Client0, Active0, Obsolete0,
                       AADDeviceID, SMBIOS_GUID0, Operating_System_Name_and0
                FROM dbo.sim_System;
            GO
            CREATE OR ALTER VIEW dbo.v_GS_PC_BIOS AS
                SELECT ResourceID, SerialNumber0 FROM dbo.sim_System;
            GO
            """);

        // Remaining views exist with their key column so access checks and grants behave as in a site.
        foreach (var view in Nexus.Collectors.Sccm.SccmViews.All.Except(["v_R_System", "v_GS_PC_BIOS"]))
        {
            sb.AppendLine($"CREATE OR ALTER VIEW dbo.{view} AS SELECT ResourceID FROM dbo.sim_System WHERE 1 = 0;");
            sb.AppendLine("GO");
        }

        sb.AppendLine("DELETE FROM dbo.sim_System;");
        foreach (var s in estate.SccmSystems)
        {
            estate.Serials.TryGetValue(s.ResourceId, out var serial);
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
                .Append(Text(serial)).AppendLine(");");
        }

        sb.AppendLine("GO");
        return sb.ToString();
    }

    private static string Text(string? value) => value is null ? "NULL" : $"N'{value.Replace("'", "''")}'";

    private static string Flag(bool? value) => value is null ? "NULL" : value.Value ? "1" : "0";
}
