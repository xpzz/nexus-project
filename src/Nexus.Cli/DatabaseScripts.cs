using System.Text;
using Nexus.Core.Sql;

namespace Nexus.Cli;

public static class DatabaseScripts
{
    /// <summary>
    /// Logins and least privilege on the Nexus database (SQL Server): read/write data, no DDL.
    /// Migrations run with the identity of whoever installs/updates, or by the DBA. Accounts are matched by SID.
    /// </summary>
    public static string SqlServerGrants(string database, IEnumerable<string> accounts)
    {
        SqlAccessScript.ValidateDatabase(database);

        var sb = new StringBuilder();
        sb.AppendLine("-- Azul Nexus: acesso das contas dos serviços ao banco do Nexus. Idempotente.");
        sb.AppendLine("USE [master];");
        sb.AppendLine($"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}];");
        sb.AppendLine("GO");
        foreach (var account in accounts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SqlAccessScript.EnsureLoginAndUser(sb, database, account);
            SqlAccessScript.AddToRole(sb, "db_datareader");
            SqlAccessScript.AddToRole(sb, "db_datawriter");
            sb.AppendLine("GO");
        }

        return sb.ToString();
    }
}
