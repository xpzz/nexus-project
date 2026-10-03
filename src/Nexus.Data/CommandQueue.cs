using Microsoft.EntityFrameworkCore;
using Nexus.Data.Entities;

namespace Nexus.Data;

public static class CommandQueue
{
    public static async Task<WorkerCommand> EnqueueAsync(NexusDbContext db, string type, string? argument, string requestedBy, CancellationToken cancellationToken)
    {
        var command = new WorkerCommand
        {
            Type = type,
            Argument = argument,
            RequestedBy = requestedBy,
            RequestedAt = DateTimeOffset.UtcNow,
            Status = CommandStatus.Pending,
        };
        db.Commands.Add(command);
        await db.SaveChangesAsync(cancellationToken);
        return command;
    }

    /// <summary>
    /// Claims the oldest pending command. The conditional update makes the claim safe even if
    /// two Worker processes run by mistake: only one of them changes the row.
    /// </summary>
    public static async Task<WorkerCommand?> ClaimNextAsync(NexusDbContext db, CancellationToken cancellationToken)
    {
        var candidate = await db.Commands.AsNoTracking()
            .Where(c => c.Status == CommandStatus.Pending)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var claimed = await db.Commands
            .Where(c => c.Id == candidate.Id && c.Status == CommandStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatus.Running).SetProperty(c => c.StartedAt, now), cancellationToken);
        if (claimed == 0)
        {
            return null;
        }

        candidate.Status = CommandStatus.Running;
        candidate.StartedAt = now;
        return candidate;
    }

    public static Task CompleteAsync(NexusDbContext db, long id, bool succeeded, string? result, CancellationToken cancellationToken) =>
        db.Commands.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, succeeded ? CommandStatus.Succeeded : CommandStatus.Failed)
            .SetProperty(c => c.CompletedAt, DateTimeOffset.UtcNow)
            .SetProperty(c => c.Result, result), cancellationToken);

    /// <summary>Waits until the Worker finishes the command, or returns null on timeout.</summary>
    public static async Task<WorkerCommand?> WaitAsync(Func<NexusDbContext> dbFactory, long id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using (var db = dbFactory())
            {
                var command = await db.Commands.AsNoTracking().FirstAsync(c => c.Id == id, cancellationToken);
                if (command.Status is CommandStatus.Succeeded or CommandStatus.Failed)
                {
                    return command;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return null;
    }
}
