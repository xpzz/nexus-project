using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Cli;

public static partial class DatabaseScripts
{
    /// <summary>
    /// Logins and least privilege on the Nexus database (SQL Server): read/write data, no DDL.
    /// Migrations run with the identity of whoever installs/updates, or by the DBA.
    /// </summary>
    public static string SqlServerGrants(string database, IEnumerable<string> accounts)
    {
        if (!Name().IsMatch(database))
        {
            throw new ArgumentException($"Nome de banco inválido: '{database}'.", nameof(database));
        }

        var sb = new StringBuilder();
        sb.AppendLine("-- Azul Nexus: acesso das contas dos serviços ao banco do Nexus. Idempotente.");
        sb.AppendLine("USE [master];");
        sb.AppendLine($"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}];");
        sb.AppendLine("GO");
        foreach (var account in accounts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Account().IsMatch(account))
            {
                throw new ArgumentException($"Conta inválida: '{account}'.", nameof(accounts));
            }

            sb.AppendLine("USE [master];");
            sb.AppendLine($"IF SUSER_ID(N'{account}') IS NULL CREATE LOGIN [{account}] FROM WINDOWS;");
            sb.AppendLine($"USE [{database}];");
            sb.AppendLine($"IF USER_ID(N'{account}') IS NULL CREATE USER [{account}] FOR LOGIN [{account}];");
            sb.AppendLine($"ALTER ROLE [db_datareader] ADD MEMBER [{account}];");
            sb.AppendLine($"ALTER ROLE [db_datawriter] ADD MEMBER [{account}];");
            sb.AppendLine("GO");
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^[A-Za-z0-9 ._-]{1,64}\\[A-Za-z0-9 ._-]{1,64}\$?$")]
    private static partial Regex Account();
}
