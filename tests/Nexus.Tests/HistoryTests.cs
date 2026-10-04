using Microsoft.EntityFrameworkCore;
using Nexus.Data.Entities;
using Nexus.Data.Support;
using Nexus.Reconciliation;

namespace Nexus.Tests;

public class AssetHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static Asset A(Guid id, Action<Asset>? set = null)
    {
        var a = new Asset { Id = id, Name = "AZ-NB-1", Serial = "S1", OperationalState = OperationalStates.Confirmed, AssetType = "notebook" };
        set?.Invoke(a);
        return a;
    }

    [Fact]
    public void FirstLoadRecordsNothing() =>
        Assert.Empty(AssetHistory.Diff(new Dictionary<Guid, Asset>(), [A(Guid.NewGuid())], Now, Guid.NewGuid()));

    [Fact]
    public void RecordsFieldChangesCreationsAndRemovals()
    {
        var (kept, gone, born) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var before = new Dictionary<Guid, Asset> { [kept] = A(kept), [gone] = A(gone, a => a.Name = "OLD") };
        var after = new[] { A(kept, a => { a.OperationalState = OperationalStates.NoRecent; a.PrimaryUser = "ana@azul.corp"; }), A(born, a => a.Name = "NEW") };

        var changes = AssetHistory.Diff(before, after, Now, Guid.NewGuid());

        Assert.Contains(changes, c => c.AssetId == kept && c.Field == "OperationalState" && c.OldValue == OperationalStates.Confirmed && c.NewValue == OperationalStates.NoRecent);
        Assert.Contains(changes, c => c.AssetId == kept && c.Field == "PrimaryUser" && c.OldValue is null && c.NewValue == "ana@azul.corp");
        Assert.Contains(changes, c => c.AssetId == born && c.Field == "Created");
        Assert.Contains(changes, c => c.AssetId == gone && c.Field == "Removed" && c.AssetName == "OLD");
        Assert.DoesNotContain(changes, c => c.Field == "Serial");
    }

    [Fact]
    public void UnchangedAssetsProduceNoChanges()
    {
        var id = Guid.NewGuid();
        Assert.Empty(AssetHistory.Diff(new Dictionary<Guid, Asset> { [id] = A(id) }, [A(id)], Now, Guid.NewGuid()));
    }

    [Fact]
    public void TimelineWritesNewAssetsAndMovedDatesOnly()
    {
        var (steady, moved, fresh) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var before = new Dictionary<Guid, Asset>
        {
            [steady] = A(steady, a => a.SccmLastSeenAt = Now.AddDays(-1)),
            [moved] = A(moved, a => a.SccmLastSeenAt = Now.AddDays(-3)),
        };
        var after = new[]
        {
            A(steady, a => a.SccmLastSeenAt = Now.AddDays(-1).AddHours(2)), // under 12 h: same day, no new entry
            A(moved, a => { a.SccmLastSeenAt = Now.AddDays(-1); a.IntuneLastSyncAt = Now.AddHours(-5); }),
            A(fresh, a => a.AdLastLogonAt = Now.AddDays(-9)),
        };

        var entries = AssetHistory.Timeline(before, after, Now);

        Assert.DoesNotContain(entries, e => e.AssetId == steady);
        Assert.Contains(entries, e => e.AssetId == moved && e.Source == "sccm");
        Assert.Contains(entries, e => e.AssetId == moved && e.Source == "intune");
        Assert.Contains(entries, e => e.AssetId == fresh && e.Source == "ad");
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void SeedWritesEveryKnownDateOnce()
    {
        var id = Guid.NewGuid();
        var asset = A(id, a => { a.SccmLastSeenAt = Now.AddDays(-1); a.XdrLastSeenAt = Now.AddDays(-2); });
        Assert.Equal(2, AssetHistory.Timeline(new Dictionary<Guid, Asset> { [id] = asset }, [asset], Now, seed: true).Count);
    }
}

public class RawRecordArchiveTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private readonly TestDb _db = new();
    private readonly Guid _run = Guid.NewGuid();

    public void Dispose() => _db.Dispose();

    private static IntuneDeviceRecord Device(string id, Action<IntuneDeviceRecord>? set = null)
    {
        var r = new IntuneDeviceRecord { Id = id, DeviceName = "AZ-NB-1", SerialNumber = "S1", ComplianceState = "compliant", LastSyncAt = T0, CollectedAt = T0 };
        set?.Invoke(r);
        return r;
    }

    private async Task<RawRecordArchive.Result> Archive(IReadOnlyList<IntuneDeviceRecord> rows, DateTimeOffset at)
    {
        await using var db = _db.Create();
        return await RawRecordArchive.ArchiveAsync(db, "intune", rows, r => r.Id, at, _run, default);
    }

    [Fact]
    public async Task FirstArchiveCreatesOneCurrentVersionPerRecord()
    {
        var result = await Archive([Device("a"), Device("b")], T0);
        Assert.Equal(2, result.Created);
        await using var db = _db.Create();
        Assert.Equal(2, await db.RawRecordVersions.CountAsync(v => v.IsCurrent));
    }

    [Fact]
    public async Task OnlyVolatileDatesChangingDoesNotCreateAVersion()
    {
        await Archive([Device("a")], T0);
        var result = await Archive([Device("a", r => { r.LastSyncAt = T0.AddHours(6); r.CollectedAt = T0.AddHours(6); })], T0.AddHours(6));
        Assert.Equal(0, result.Changed);
        Assert.Equal(1, result.Unchanged);
        await using var db = _db.Create();
        var version = await db.RawRecordVersions.SingleAsync();
        Assert.Equal(T0.AddHours(6), version.LastSeenAt);
        Assert.Equal(T0, version.FirstSeenAt);
    }

    [Fact]
    public async Task ChangedAttributeKeepsBothVersions()
    {
        await Archive([Device("a")], T0);
        var result = await Archive([Device("a", r => r.ComplianceState = "noncompliant")], T0.AddDays(1));
        Assert.Equal(1, result.Changed);
        await using var db = _db.Create();
        var versions = await db.RawRecordVersions.OrderBy(v => v.Id).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.False(versions[0].IsCurrent);
        Assert.Contains("\"compliant\"", versions[0].PayloadJson);
        Assert.True(versions[1].IsCurrent);
        Assert.Contains("noncompliant", versions[1].PayloadJson);
    }

    [Fact]
    public async Task RecordsTheSourceStoppedReturningAreClosedNotDeleted()
    {
        await Archive([Device("a"), Device("b")], T0);
        var result = await Archive([Device("a")], T0.AddDays(1));
        Assert.Equal(1, result.Removed);
        await using var db = _db.Create();
        var b = await db.RawRecordVersions.SingleAsync(v => v.SourceKey == "b");
        Assert.False(b.IsCurrent);
        Assert.Equal(T0.AddDays(1), b.RemovedAt);
    }

    [Fact]
    public async Task RecordsWithoutKeyAreSkippedAndDuplicateKeysKeepTheFirst()
    {
        var result = await Archive([Device(""), Device("a", r => r.DeviceName = "FIRST"), Device("a", r => r.DeviceName = "SECOND")], T0);
        Assert.Equal(1, result.Created);
        await using var db = _db.Create();
        Assert.Contains("FIRST", (await db.RawRecordVersions.SingleAsync()).PayloadJson);
    }

    [Fact]
    public async Task PurgeKeepsCurrentVersionsAndDropsOldSuperseded()
    {
        await Archive([Device("a")], T0);
        await Archive([Device("a", r => r.ComplianceState = "noncompliant")], T0.AddDays(1));
        await using var db = _db.Create();
        var purged = await RawRecordArchive.PurgeAsync(db, T0.AddDays(500), 400, default);
        Assert.Equal(1, purged);
        Assert.Equal(1, await db.RawRecordVersions.CountAsync());
        Assert.True((await db.RawRecordVersions.SingleAsync()).IsCurrent);
    }

    [Fact]
    public void HashIgnoresVolatileDatesAndPropertyOrder()
    {
        var a = RawRecordArchive.HashOf("{\"Id\":\"1\",\"LastSyncAt\":\"2026-01-01T00:00:00Z\",\"Name\":\"X\"}");
        var b = RawRecordArchive.HashOf("{\"Name\":\"X\",\"Id\":\"1\",\"LastSyncAt\":\"2027-05-05T00:00:00Z\",\"CollectedAt\":\"2027-05-05T00:00:00Z\"}");
        var c = RawRecordArchive.HashOf("{\"Name\":\"Y\",\"Id\":\"1\"}");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
