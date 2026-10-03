using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Nexus.Core.Configuration;
using Nexus.Core.Security;
using Npgsql;

namespace Nexus.Data;

public static class NexusDatabase
{
    public const string MigrationsHistoryTable = "__nexus_migrations";

    public static NexusDbContext Create(DatabaseSettings settings, ISecretProtector? protector = null) =>
        settings.Provider switch
        {
            DatabaseProvider.SqlServer => new SqlServerNexusDbContext(
                new DbContextOptionsBuilder<SqlServerNexusDbContext>()
                    .UseSqlServer(settings.ConnectionString, o => o.MigrationsHistoryTable(MigrationsHistoryTable).EnableRetryOnFailure())
                    .Options),
            DatabaseProvider.PostgreSql => new PostgresNexusDbContext(
                new DbContextOptionsBuilder<PostgresNexusDbContext>()
                    .UseNpgsql(BuildPostgresConnectionString(settings, protector), o => o.MigrationsHistoryTable(MigrationsHistoryTable).EnableRetryOnFailure())
                    .Options),
            _ => throw new NotSupportedException($"Provedor de banco não suportado: {settings.Provider}."),
        };

    /// <summary>Applies pending migrations. Returns the migrations applied in this call.</summary>
    public static async Task<IReadOnlyList<string>> MigrateAsync(NexusDbContext db, CancellationToken cancellationToken)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        if (!await db.Setup.AnyAsync(cancellationToken))
        {
            db.Setup.Add(new Entities.SetupState { Id = 1, SetupModeActive = true });
            await db.SaveChangesAsync(cancellationToken);
        }

        return pending;
    }

    /// <summary>Database name extracted from the connection string, for messages and backups.</summary>
    public static string DatabaseName(DatabaseSettings settings) => settings.Provider switch
    {
        DatabaseProvider.SqlServer => new SqlConnectionStringBuilder(settings.ConnectionString).InitialCatalog,
        DatabaseProvider.PostgreSql => new NpgsqlConnectionStringBuilder(settings.ConnectionString).Database ?? "",
        _ => "",
    };

    private static string BuildPostgresConnectionString(DatabaseSettings settings, ISecretProtector? protector)
    {
        var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
        if (settings.ProtectedPassword is { Length: > 0 } protectedPassword)
        {
            builder.Password = (protector ?? new DpapiSecretProtector()).Unprotect(protectedPassword);
        }
        else if (Environment.GetEnvironmentVariable("NEXUS_DB_PASSWORD") is { Length: > 0 } developmentPassword)
        {
            builder.Password = developmentPassword;
        }

        return builder.ConnectionString;
    }
}

// Design-time factories used only by 'dotnet ef migrations add'. They never connect.
public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerNexusDbContext>
{
    public SqlServerNexusDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SqlServerNexusDbContext>()
            .UseSqlServer("Server=design-time;Database=AzulNexus;Integrated Security=true", o => o.MigrationsHistoryTable(NexusDatabase.MigrationsHistoryTable))
            .Options);
}

public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresNexusDbContext>
{
    public PostgresNexusDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PostgresNexusDbContext>()
            .UseNpgsql("Host=design-time;Database=azulnexus", o => o.MigrationsHistoryTable(NexusDatabase.MigrationsHistoryTable))
            .Options);
}
