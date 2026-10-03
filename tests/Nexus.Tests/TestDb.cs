using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nexus.Data;
using Nexus.Data.Support;

namespace Nexus.Tests;

/// <summary>SQLite in memory for fast unit tests. Real providers are covered by the integration tests.</summary>
public sealed class TestNexusDbContext(DbContextOptions<TestNexusDbContext> options) : NexusDbContext(options);

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
