using System.Text;
using Nexus.Core.Sql;

namespace Nexus.Collectors.Xdr;

/// <summary>
/// T-SQL for the DBA: a dedicated role with SELECT on the one table the Nexus reads, in the XDR database.
/// Idempotent and reversible; the account is matched by SID (same approach as the SCCM grant).
/// </summary>
public static class XdrGrantScript
{
    public const string ReaderRole = "azul_nexus_xdr_reader";

    public static string Grant(string database, string table, string account)
    {
        var quotedTable = XdrIdentifiers.QuotedTable(table);
        var sb = new StringBuilder();
        sb.AppendLine("-- Azul Nexus: leitura da tabela de endpoints do Cortex XDR.");
        sb.AppendLine($"-- Conta: {account}  Banco: {database}  Tabela: {table}");
        sb.AppendLine("-- Idempotente: pode ser executado mais de uma vez.");
        SqlAccessScript.EnsureLoginAndUser(sb, XdrIdentifiers.Database(database), account);
        sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{ReaderRole}') IS NULL CREATE ROLE [{ReaderRole}];");
        sb.AppendLine($"IF OBJECT_ID(N'{quotedTable}') IS NOT NULL GRANT SELECT ON {quotedTable} TO [{ReaderRole}];");
        SqlAccessScript.AddToRole(sb, ReaderRole);
        return sb.ToString();
    }

    public static string Revoke(string database, string account)
    {
        SqlAccessScript.ValidateDatabase(XdrIdentifiers.Database(database));
        SqlAccessScript.ValidateAccount(account);
        var sb = new StringBuilder();
        sb.AppendLine("-- Azul Nexus: remove o acesso de leitura criado pelo script de concessão do XDR.");
        sb.AppendLine("USE [master];");
        sb.AppendLine($"DECLARE @sid varbinary(85) = SUSER_SID(N'{account}');");
        sb.AppendLine($"USE [{database}];");
        sb.AppendLine("DECLARE @user sysname = (SELECT TOP (1) name FROM sys.database_principals WHERE sid = @sid);");
        sb.AppendLine("DECLARE @sql nvarchar(max);");
        sb.AppendLine($"IF @user IS NOT NULL AND @user <> N'dbo' AND DATABASE_PRINCIPAL_ID(N'{ReaderRole}') IS NOT NULL");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"    SET @sql = N'ALTER ROLE [{ReaderRole}] DROP MEMBER ' + QUOTENAME(@user);");
        sb.AppendLine("    EXEC (@sql);");
        sb.AppendLine("END;");
        sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{ReaderRole}') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.database_role_members WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'{ReaderRole}')) DROP ROLE [{ReaderRole}];");
        return sb.ToString();
    }
}
