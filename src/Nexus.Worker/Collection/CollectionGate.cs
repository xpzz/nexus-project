using Nexus.Core.Configuration;
using Nexus.Core.Errors;
using Nexus.Worker.Platform;

namespace Nexus.Worker.Collection;

/// <summary>Decides whether a collection may start now: manual pause, pause windows and server load.</summary>
public sealed class CollectionGate(IServerLoad serverLoad, TimeProvider clock)
{
    public async Task<NexusError?> CheckAsync(CollectionSettings settings, bool slowQueryObserved, CancellationToken cancellationToken)
    {
        if (settings.Paused)
        {
            return new NexusError("NEXUS-COL-000", "Coletores pausados manualmente.",
                "Os dados seguem com o último resultado válido.", "Retome em Saúde › Retomar coletores ou com 'nexusctl resume'.");
        }

        var localNow = clock.GetLocalNow().DateTime;
        var window = settings.PauseWindows.FirstOrDefault(w => w.Contains(localNow));
        if (window is not null)
        {
            return ErrorCatalog.CollectionPausedByWindow.WithDetail($"Janela '{window.Name}' ({window.Start:HH\\:mm}–{window.End:HH\\:mm}).");
        }

        if (slowQueryObserved)
        {
            return ErrorCatalog.CollectionBackoff.WithDetail("A última consulta ao SQL do site passou do limite de duração.");
        }

        var cpu = await serverLoad.GetCpuPercentAsync(cancellationToken);
        if (cpu is { } value && value >= settings.ServerCpuBackoffPercent)
        {
            return ErrorCatalog.CollectionBackoff.WithDetail($"CPU do servidor em {value}% (limite {settings.ServerCpuBackoffPercent}%).");
        }

        return null;
    }
}
