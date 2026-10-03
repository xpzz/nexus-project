using System.Text;
using Nexus.Core.Sql;

namespace Nexus.Collectors.Sccm;

/// <summary>
/// T-SQL for the DBA: dedicated read-only role with explicit SELECT on the views used (SPEC §6.1).
/// Idempotent and reversible; never changes the schema of the site database. The account is matched by SID,
/// so it also works when the account already has a login or a user (even as <c>dbo</c>) in the site database.
/// </summary>
public static class SccmGrantScript
{
    public static string Grant(string database, string account)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- Azul Nexus: leitura das views do Configuration Manager.");
        sb.AppendLine($"-- Conta: {account}  Banco: {database}");
        sb.AppendLine("-- Idempotente: pode ser executado mais de uma vez. Reversão: script de revogação gerado junto.");
        SqlAccessScript.EnsureLoginAndUser(sb, database, account);
        sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{SccmViews.ReaderRole}') IS NULL CREATE ROLE [{SccmViews.ReaderRole}];");
        foreach (var view in SccmViews.All)
        {
            sb.AppendLine($"IF OBJECT_ID(N'dbo.{view}') IS NOT NULL GRANT SELECT ON [dbo].[{view}] TO [{SccmViews.ReaderRole}];");
        }

        SqlAccessScript.AddToRole(sb, SccmViews.ReaderRole);
        return sb.ToString();
    }

    public static string Revoke(string database, string account)
    {
        SqlAccessScript.ValidateDatabase(database);
        SqlAccessScript.ValidateAccount(account);
        var role = SccmViews.ReaderRole;
        var sb = new StringBuilder();
        sb.AppendLine("-- Azul Nexus: remove o acesso de leitura criado pelo script de concessão.");
        sb.AppendLine("-- O login e o usuário da conta NÃO são removidos (podem existir por outros motivos); remova-os à parte se quiser.");
        sb.AppendLine("USE [master];");
        sb.AppendLine($"DECLARE @sid varbinary(85) = SUSER_SID(N'{account}');");
        sb.AppendLine($"USE [{database}];");
        sb.AppendLine("DECLARE @user sysname = (SELECT TOP (1) name FROM sys.database_principals WHERE sid = @sid);");
        sb.AppendLine("DECLARE @sql nvarchar(max);");
        sb.AppendLine($"IF @user IS NOT NULL AND @user <> N'dbo' AND DATABASE_PRINCIPAL_ID(N'{role}') IS NOT NULL");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"    SET @sql = N'ALTER ROLE [{role}] DROP MEMBER ' + QUOTENAME(@user);");
        sb.AppendLine("    EXEC (@sql);");
        sb.AppendLine("END;");
        sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{role}') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.database_role_members WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'{role}')) DROP ROLE [{role}];");
        return sb.ToString();
    }
}
