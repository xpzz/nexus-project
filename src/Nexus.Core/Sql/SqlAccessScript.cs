using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Core.Sql;

/// <summary>
/// T-SQL fragments shared by the grant scripts. The Windows account is matched by SID, never by name:
/// a login may already exist under another spelling (DOMAIN\user vs user@domain), and a database user
/// may already exist under another name (for example <c>dbo</c> when the account owns the database),
/// which makes a name-based CREATE USER fail with error 15063.
/// </summary>
public static partial class SqlAccessScript
{
    public static void ValidateDatabase(string database)
    {
        if (!DatabaseName().IsMatch(database))
        {
            throw new ArgumentException($"Nome de banco inválido: '{database}'. Esperado algo como AzulNexus ou CM_ABC.", nameof(database));
        }
    }

    public static void ValidateAccount(string account)
    {
        if (!AccountName().IsMatch(account))
        {
            throw new ArgumentException($"Conta inválida: '{account}'. Esperado DOMINIO\\conta, DOMINIO\\gmsa$ ou NT SERVICE\\AzulNexus.Worker.", nameof(account));
        }
    }

    /// <summary>
    /// One batch: ensures the server login, switches to <paramref name="database"/> and leaves <c>@user</c> holding the
    /// database user mapped to the account (created when missing). Must be followed by statements of the same batch.
    /// </summary>
    public static void EnsureLoginAndUser(StringBuilder sb, string database, string account)
    {
        ValidateDatabase(database);
        ValidateAccount(account);
        sb.AppendLine("USE [master];");
        sb.AppendLine($"DECLARE @sid varbinary(85) = SUSER_SID(N'{account}');");
        sb.AppendLine($"IF @sid IS NULL OR NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE sid = @sid) CREATE LOGIN [{account}] FROM WINDOWS;");
        sb.AppendLine($"SET @sid = SUSER_SID(N'{account}');");
        sb.AppendLine("DECLARE @login sysname = (SELECT TOP (1) name FROM sys.server_principals WHERE sid = @sid);");
        sb.AppendLine($"USE [{database}];");
        sb.AppendLine("DECLARE @user sysname = (SELECT TOP (1) name FROM sys.database_principals WHERE sid = @sid);");
        sb.AppendLine("IF @user IS NULL");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"    EXEC (N'CREATE USER ' + QUOTENAME(N'{account}') + N' FOR LOGIN ' + QUOTENAME(@login));");
        sb.AppendLine($"    SET @user = N'{account}';");
        sb.AppendLine("END;");
    }

    /// <summary>Adds the mapped user to a role. <c>dbo</c> (database owner) already has full access and is skipped.</summary>
    public static void AddToRole(StringBuilder sb, string role)
    {
        sb.AppendLine($"IF @user <> N'dbo' EXEC (N'ALTER ROLE [{role}] ADD MEMBER ' + QUOTENAME(@user));");
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex DatabaseName();

    [GeneratedRegex(@"^[A-Za-z0-9 ._-]{1,64}\\[A-Za-z0-9 ._-]{1,64}\$?$")]
    private static partial Regex AccountName();
}
