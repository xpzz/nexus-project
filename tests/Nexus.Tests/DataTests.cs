using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nexus.Data;
using Nexus.Data.Entities;

namespace Nexus.Tests;

public class SetupModeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task CodeWorksOnlyOnce()
    {
        await using var db = _db.Create();
        var code = await SetupModeService.IssueCodeAsync(db, "admin", _clock, default);
        Assert.True(await SetupModeService.RedeemCodeAsync(db, code, "10.0.0.5", _clock, default));
        Assert.False(await SetupModeService.RedeemCodeAsync(db, code, "10.0.0.5", _clock, default));
    }

    [Fact]
    public async Task CodeExpiresAfter24Hours()
    {
        await using var db = _db.Create();
        var code = await SetupModeService.IssueCodeAsync(db, "admin", _clock, default);
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        Assert.False(await SetupModeService.RedeemCodeAsync(db, code, "10.0.0.5", _clock, default));
    }

    [Fact]
    public async Task NewCodeInvalidatesPreviousOne()
    {
        await using var db = _db.Create();
        var first = await SetupModeService.IssueCodeAsync(db, "admin", _clock, default);
        await SetupModeService.IssueCodeAsync(db, "admin", _clock, default);
        Assert.False(await SetupModeService.RedeemCodeAsync(db, first, "10.0.0.5", _clock, default));
    }

    [Fact]
    public async Task EndedSetupModeRejectsCodesAndRecoverAccessReopensWithAudit()
    {
        await using var db = _db.Create();
        var code = await SetupModeService.IssueCodeAsync(db, "admin", _clock, default);
        await SetupModeService.EndAsync(db, "sso", default);
        Assert.False(await SetupModeService.RedeemCodeAsync(db, code, "10.0.0.5", _clock, default));

        var recovered = await SetupModeService.RecoverAccessAsync(db, @"SRV\Administrador", _clock, default);
        Assert.True((await SetupModeService.GetAsync(db, default)).SetupModeActive);
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "setup.mode.recovered" && a.Actor == @"SRV\Administrador"));
        Assert.True(await SetupModeService.RedeemCodeAsync(db, recovered, "10.0.0.5", _clock, default));
    }

    [Fact]
    public async Task OnlyTheHashIsStored()
    {
        await using var db = _db.Create();
        var code = await SetupModeService.IssueCodeAsync(db, "admin", _clock, default);
        var state = await db.Setup.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(code.Replace("-", ""), state.CodeHash);
    }

    public void Dispose() => _db.Dispose();
}

public class CommandQueueTests : IDisposable
{
    private readonly TestDb _db = new();

    [Fact]
    public async Task CommandIsClaimedOnceAndCompleted()
    {
        await using var db = _db.Create();
        var command = await CommandQueue.EnqueueAsync(db, CommandTypes.CollectNow, "sccm", "web:local", default);

        var claimed = await CommandQueue.ClaimNextAsync(_db.Create(), default);
        Assert.Equal(command.Id, claimed?.Id);
        Assert.Null(await CommandQueue.ClaimNextAsync(_db.Create(), default));

        await CommandQueue.CompleteAsync(_db.Create(), command.Id, succeeded: true, "ok", default);
        var done = await CommandQueue.WaitAsync(_db.Create, command.Id, TimeSpan.FromSeconds(2), default);
        Assert.Equal(CommandStatus.Succeeded, done?.Status);
    }

    [Fact]
    public async Task WaitReturnsNullWhenWorkerDoesNotAnswer()
    {
        await using var db = _db.Create();
        var command = await CommandQueue.EnqueueAsync(db, CommandTypes.RunHealthChecks, null, "cli", default);
        Assert.Null(await CommandQueue.WaitAsync(_db.Create, command.Id, TimeSpan.FromMilliseconds(200), default));
    }

    public void Dispose() => _db.Dispose();
}
