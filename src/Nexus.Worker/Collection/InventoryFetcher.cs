using Microsoft.EntityFrameworkCore;
using Nexus.Collectors.Sccm;
using Nexus.Data;
using Nexus.Data.Entities;
using Nexus.Data.Support;

namespace Nexus.Worker.Collection;

/// <summary>
/// Reads the installed software of one device, on demand, from SCCM (Add/Remove Programs) and Intune (detected apps).
/// It is too large to collect for the whole estate, so it is fetched when someone opens the device and cached in the Nexus database.
/// </summary>
public sealed class InventoryFetcher(INexusDbFactory dbFactory, SettingsProvider settingsProvider, ISourceFactory sources, TimeProvider clock, ILogger<InventoryFetcher> logger)
{
    public async Task<string> FetchAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var settings = settingsProvider.Current;
        List<AssetLink> links;
        await using (var db = dbFactory.Create())
        {
            links = await db.AssetLinks.AsNoTracking().Where(l => l.AssetId == assetId && (l.Source == "sccm" || l.Source == "intune")).ToListAsync(cancellationToken);
        }

        var rows = new List<InstalledSoftwareRecord>();
        var notes = new List<string>();

        if (links.FirstOrDefault(l => l.Source == "sccm") is { } sccm && int.TryParse(sccm.SourceKey, out var resourceId))
        {
            try
            {
                var reader = sources.CreateSccmReader(settings.Sccm, new SccmQueryGate(settings.Sccm));
                if (reader is not null)
                {
                    rows.AddRange((await reader.ReadSoftwareAsync(resourceId, cancellationToken)).Select(s => new InstalledSoftwareRecord
                    {
                        AssetId = assetId, Source = "sccm", Name = s.Name, Version = s.Version, Publisher = s.Publisher, InstalledOn = s.InstalledOn,
                    }));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Inventário de software do SCCM falhou para {Asset}", assetId);
                notes.Add("SCCM: " + ex.Message);
            }
        }

        if (links.FirstOrDefault(l => l.Source == "intune") is { } intune)
        {
            try
            {
                var graph = sources.CreateGraphReader(settings);
                if (graph is not null)
                {
                    rows.AddRange((await graph.ReadDetectedAppsAsync(intune.SourceKey, cancellationToken)).Select(a => new InstalledSoftwareRecord
                    {
                        AssetId = assetId, Source = "intune", Name = a.Name, Version = a.Version, Publisher = a.Publisher, SizeBytes = a.SizeBytes,
                    }));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Aplicativos detectados do Intune falharam para {Asset}", assetId);
                notes.Add("Intune: " + ex.Message);
            }
        }

        await using (var db = dbFactory.Create())
        {
            await db.InstalledSoftware.Where(r => r.AssetId == assetId).ExecuteDeleteAsync(cancellationToken);
            db.InstalledSoftware.AddRange(rows);
            var fetch = await db.InventoryFetches.FirstOrDefaultAsync(f => f.AssetId == assetId, cancellationToken);
            if (fetch is null)
            {
                fetch = new InventoryFetch { AssetId = assetId, RequestedAt = clock.GetUtcNow() };
                db.InventoryFetches.Add(fetch);
            }

            fetch.FetchedAt = clock.GetUtcNow();
            fetch.Status = notes.Count > 0 && rows.Count == 0 ? "Failed" : "Done";
            fetch.Message = notes.Count > 0 ? string.Join(" | ", notes) : $"{rows.Count} programas.";
            await db.SaveChangesAsync(cancellationToken);
        }

        return $"{rows.Count} programas" + (notes.Count > 0 ? " (com avisos: " + string.Join(" | ", notes) + ")" : "");
    }
}
