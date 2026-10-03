using Nexus.Data.Support;
using Microsoft.EntityFrameworkCore;
using Nexus.Worker.Collection;

namespace Nexus.Worker;

/// <summary>Starts due collections. Next run times are persisted, so restarts resume the schedule.</summary>
public sealed class Scheduler(
    INexusDbFactory dbFactory,
    SettingsProvider settingsProvider,
    JobRunner jobs,
    TimeProvider clock,
    ILogger<Scheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (settingsProvider.Exists)
                {
                    await RunDueJobsAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Falha no agendador; nova tentativa no próximo ciclo.");
            }

            await Task.Delay(Tick, stoppingToken);
        }
    }

    public async Task RunDueJobsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, DateTimeOffset?> nextRuns;
        await using (var db = dbFactory.Create())
        {
            nextRuns = await db.Jobs.ToDictionaryAsync(j => j.Name, j => j.NextRunAt, cancellationToken);
        }

        var now = clock.GetUtcNow();
        foreach (var job in JobNames.All)
        {
            if (!nextRuns.TryGetValue(job, out var next) || next is null || next <= now)
            {
                await jobs.RunAsync(job, cancellationToken);
            }
        }
    }
}
