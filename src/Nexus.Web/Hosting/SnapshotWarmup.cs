using Nexus.Data.Support;
using Nexus.Reconciliation;

namespace Nexus.Web.Hosting;

/// <summary>Loads the inventory snapshot at startup and keeps it fresh, so no visitor ever waits for the database.</summary>
public sealed class SnapshotWarmup(InventorySnapshotService snapshots, SettingsProvider settings, ILogger<SnapshotWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (settings.Exists)
                {
                    await snapshots.GetAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Não foi possível carregar o inventário; nova tentativa em instantes.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
