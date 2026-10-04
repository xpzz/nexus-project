using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nexus.Data;
using Nexus.Data.Support;

namespace Nexus.Tests;

/// <summary>SQLite in memory for fast unit tests. Real providers are covered by the integration tests.</summary>
public sealed class TestNexusDbContext(DbContextOptions<TestNexusDbContext> options) : NexusDbContext(options)
{
    /// <summary>SQLite cannot compare DateTimeOffset in SQL; storing them as binary lets the tests run the same range queries SQL Server and PostgreSQL run.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
    }
}

public sealed class TestDb : INexusDbFactory, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb()
    {
        _connection.Open();
        using var db = Create();
        db.Database.EnsureCreated();
    }

    public NexusDbContext Create() =>
        new TestNexusDbContext(new DbContextOptionsBuilder<TestNexusDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}
