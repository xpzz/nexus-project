using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Collectors.Sccm;

/// <summary>
/// T-SQL for the DBA: dedicated read-only role with explicit SELECT on the views used (SPEC §6.1).
/// Idempotent and reversible; never changes the schema of the site database.
/// </summary>
public static partial class SccmGrantScript
{
    public static string Grant(string database, string account)
    {
        Validate(database, account);
        var sb = new StringBuilder();
        sb.AppendLine("-- Azul Nexus: leitura das views do Configuration Manager.");
        sb.AppendLine($"-- Conta: {account}  Banco: {database}");
        sb.AppendLine("-- Idempotente: pode ser executado mais de uma vez. Reversão: script de revogação gerado junto.");
        sb.AppendLine("USE [master];");
        sb.AppendLine($"IF SUSER_ID(N'{account}') IS NULL CREATE LOGIN [{account}] FROM WINDOWS;");
        sb.AppendLine($"USE [{database}];");
        sb.AppendLine($"IF USER_ID(N'{account}') IS NULL CREATE USER [{account}] FOR LOGIN [{account}];");
        sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{SccmViews.ReaderRole}') IS NULL CREATE ROLE [{SccmViews.ReaderRole}];");
        foreach (var view in SccmViews.All)
        {
            sb.AppendLine($"IF OBJECT_ID(N'dbo.{view}') IS NOT NULL GRANT SELECT ON [dbo].[{view}] TO [{SccmViews.ReaderRole}];");
        }

        sb.AppendLine($"ALTER ROLE [{SccmViews.ReaderRole}] ADD MEMBER [{account}];");
        return sb.ToString();
    }

    public static string Revoke(string database, string account)
    {
        Validate(database, account);
        var sb = new StringBuilder();
        sb.AppendLine("-- Azul Nexus: remove o acesso de leitura criado pelo script de concessão.");
        sb.AppendLine($"USE [{database}];");
        sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{SccmViews.ReaderRole}') IS NOT NULL AND USER_ID(N'{account}') IS NOT NULL ALTER ROLE [{SccmViews.ReaderRole}] DROP MEMBER [{account}];");
        sb.AppendLine($"IF USER_ID(N'{account}') IS NOT NULL DROP USER [{account}];");
        sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{SccmViews.ReaderRole}') IS NOT NULL DROP ROLE [{SccmViews.ReaderRole}];");
        sb.AppendLine("-- O login no nível da instância não é removido automaticamente; remova-o se não for usado por outro banco:");
        sb.AppendLine($"-- USE [master]; DROP LOGIN [{account}];");
        return sb.ToString();
    }

    private static void Validate(string database, string account)
    {
        if (!DatabaseName().IsMatch(database))
        {
            throw new ArgumentException($"Nome de banco inválido: '{database}'. Esperado algo como CM_ABC.", nameof(database));
        }

        if (!AccountName().IsMatch(account))
        {
            throw new ArgumentException($"Conta inválida: '{account}'. Esperado DOMINIO\\conta, DOMINIO\\gmsa$ ou NT SERVICE\\AzulNexus.Worker.", nameof(account));
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex DatabaseName();

    [GeneratedRegex(@"^[A-Za-z0-9 ._-]{1,64}\\[A-Za-z0-9 ._-]{1,64}\$?$")]
    private static partial Regex AccountName();
}
