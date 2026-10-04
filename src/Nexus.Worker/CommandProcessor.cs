using Nexus.Data.Support;
using System.Text.Json;
using Nexus.Core.Configuration;
using Nexus.Data;
using Nexus.Data.Entities;
using Nexus.Worker.Collection;
using Nexus.Worker.Health;

namespace Nexus.Worker;

/// <summary>Executes commands queued by the Web and nexusctl (collect now, pause, health checks).</summary>
public sealed class CommandProcessor(
    INexusDbFactory dbFactory,
    SettingsProvider settingsProvider,
    JobRunner jobs,
    HealthRunner health,
    InventoryFetcher inventory,
    ILogger<CommandProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (settingsProvider.Exists && await ProcessNextAsync(stoppingToken))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Falha ao ler a fila de comandos; nova tentativa em instantes.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        WorkerCommand? command;
        await using (var db = dbFactory.Create())
        {
            command = await CommandQueue.ClaimNextAsync(db, cancellationToken);
        }

        if (command is null)
        {
            return false;
        }

        logger.LogInformation("Comando {Type} ({Argument}) pedido por {RequestedBy}", command.Type, command.Argument, command.RequestedBy);
        bool succeeded;
        string? result;
        try
        {
            (succeeded, result) = await ExecuteAsync(command, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha ao executar o comando {Type}", command.Type);
            (succeeded, result) = (false, ex.Message);
        }

        await using (var db = dbFactory.Create())
        {
            await CommandQueue.CompleteAsync(db, command.Id, succeeded, result, cancellationToken);
        }

        return true;
    }

    private async Task<(bool, string?)> ExecuteAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        switch (command.Type)
        {
            case CommandTypes.RunHealthChecks:
                var results = await health.RunAsync(cancellationToken);
                return (true, JsonSerializer.Serialize(results, SettingsStore.JsonOptions));

            case CommandTypes.CollectNow:
                var targets = string.IsNullOrEmpty(command.Argument) || command.Argument == "all"
                    ? JobNames.All
                    : JobNames.All.Where(j => j.StartsWith(command.Argument, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (targets.Length == 0)
                {
                    return (false, $"Coleta desconhecida: '{command.Argument}'. Use sccm, ad, intune, entra, inventory ou all.");
                }

                var outcomes = new Dictionary<string, JobOutcome>();
                foreach (var job in targets)
                {
                    outcomes[job] = await jobs.RunAsync(job, cancellationToken);
                }

                return (outcomes.Values.All(o => o.Status != JobRunner.StatusFailed), JsonSerializer.Serialize(outcomes, SettingsStore.JsonOptions));

            case CommandTypes.FetchInventory:
                if (!Guid.TryParse(command.Argument, out var assetId))
                {
                    return (false, "Identificador de dispositivo inválido.");
                }

                var fetched = await inventory.FetchAsync(assetId, cancellationToken);
                return (true, fetched);

            case CommandTypes.PauseCollectors:
            case CommandTypes.ResumeCollectors:
                // The paused flag lives in the configuration file, written by Web/nexusctl;
                // the command only records the request in the audit trail.
                await using (var db = dbFactory.Create())
                {
                    Audit.Record(db, command.RequestedBy, command.Type == CommandTypes.PauseCollectors ? "collectors.paused" : "collectors.resumed");
                    await db.SaveChangesAsync(cancellationToken);
                }

                return (true, null);

            default:
                return (false, $"Comando desconhecido: {command.Type}.");
        }
    }
}
