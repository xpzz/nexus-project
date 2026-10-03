using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Nexus.Core;
using Nexus.Core.Configuration;
using Nexus.Data;
using Npgsql;

namespace Nexus.Cli;

public static class Backup
{
    public static string Config(NexusPaths paths)
    {
        var folder = Path.Combine(paths.BackupsDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        if (File.Exists(paths.ConfigFile))
        {
            File.Copy(paths.ConfigFile, Path.Combine(folder, "nexus.json"), overwrite: true);
        }

        return folder;
    }

    public static async Task<(bool Ok, string Message)> DatabaseAsync(DatabaseSettings settings, string folder, CancellationToken ct)
    {
        var database = NexusDatabase.DatabaseName(settings);
        if (settings.Provider == DatabaseProvider.SqlServer)
        {
            // Relative file name: SQL Server writes it to its own default backup folder,
            // which works for local and remote instances.
            var file = $"{database}_nexus_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
            try
            {
                var master = new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
                await using var connection = new SqlConnection(master);
                await connection.OpenAsync(ct);
                await using var command = new SqlCommand($"IF DB_ID(N'{database}') IS NOT NULL BACKUP DATABASE [{database}] TO DISK = N'{file}' WITH COPY_ONLY, INIT;", connection)
                {
                    CommandTimeout = 3600,
                };
                await command.ExecuteNonQueryAsync(ct);
                await File.WriteAllTextAsync(Path.Combine(folder, "database-backup.txt"), $"SQL Server: {file} na pasta de backup padrão da instância.", ct);
                return (true, $"Backup do banco gravado como {file} na pasta de backup padrão do SQL Server.");
            }
            catch (SqlException ex)
            {
                return (false, $"Não foi possível fazer o backup do banco {database}: {ex.Message}\n" +
                    "Impacto: a atualização não deve migrar o banco sem backup.\n" +
                    "Como resolver: peça ao DBA um backup COPY_ONLY do banco ou conceda 'db_backupoperator' à sua conta e repita.");
            }
        }

        var pgDump = FindOnPath(OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");
        if (pgDump is null)
        {
            return (false, "pg_dump não encontrado no PATH.\nImpacto: o backup do PostgreSQL não foi feito.\n" +
                "Como resolver: peça ao DBA o backup do banco ou instale as ferramentas cliente do PostgreSQL e repita.");
        }

        var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
        var output = Path.Combine(folder, $"{database}.dump");
        var start = new ProcessStartInfo(pgDump, ["-Fc", "-h", builder.Host ?? "localhost", "-p", builder.Port.ToString(), "-U", builder.Username ?? "", "-f", output, database])
        {
            RedirectStandardError = true,
        };
        if (settings.ProtectedPassword is { Length: > 0 } protectedPassword)
        {
            start.Environment["PGPASSWORD"] = new Nexus.Core.Security.DpapiSecretProtector().Unprotect(protectedPassword);
        }

        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0
            ? (true, $"Backup do PostgreSQL gravado em {output}.")
            : (false, $"pg_dump falhou: {error}");
    }

    private static string? FindOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, executable))
            .FirstOrDefault(File.Exists);
}
