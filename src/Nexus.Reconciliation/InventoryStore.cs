using Microsoft.EntityFrameworkCore;
using Nexus.Data;
using Nexus.Data.Entities;
using Nexus.Data.Support;

namespace Nexus.Reconciliation;

/// <summary>Reads the raw tables, reconciles and replaces the derived tables in one transaction.</summary>
public sealed class InventoryReconciler(INexusDbFactory dbFactory, TimeProvider clock)
{
    public static readonly TimeSpan DefaultActivityWindow = TimeSpan.FromDays(30);

    public async Task<ReconcileResult> RunAsync(CancellationToken cancellationToken)
    {
        await using var db = dbFactory.Create();
        var input = new ReconcileInput(
            await db.SccmDevices.AsNoTracking().ToListAsync(cancellationToken),
            await db.AdComputers.AsNoTracking().ToListAsync(cancellationToken),
            await db.IntuneDevices.AsNoTracking().ToListAsync(cancellationToken),
            await db.EntraDevices.AsNoTracking().ToListAsync(cancellationToken),
            await db.AssetLinks.AsNoTracking().ToListAsync(cancellationToken),
            clock.GetUtcNow(), DefaultActivityWindow,
            await db.EntraUsers.AsNoTracking().ToListAsync(cancellationToken),
            await db.IntuneDevicePolicyStates.AsNoTracking().Select(p => new IntuneDevicePolicyState { IntuneDeviceId = p.IntuneDeviceId, Kind = p.Kind, State = p.State }).ToListAsync(cancellationToken),
            await db.MamRegistrations.AsNoTracking().ToListAsync(cancellationToken),
            await db.Jobs.AnyAsync(j => j.Name == "intune.policies" && j.LastSuccessAt != null, cancellationToken),
            await db.XdrEndpoints.AsNoTracking().ToListAsync(cancellationToken));

        var result = Reconciler.Run(input);

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.ReviewItems.ExecuteDeleteAsync(cancellationToken);
            await db.AssetLinks.ExecuteDeleteAsync(cancellationToken);
            await db.Assets.ExecuteDeleteAsync(cancellationToken);
            db.ChangeTracker.Clear();
            foreach (var chunk in result.Assets.Chunk(1000))
            {
                db.Assets.AddRange(chunk);
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            foreach (var chunk in result.Links.Chunk(1000))
            {
                db.AssetLinks.AddRange(chunk);
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            db.ReviewItems.AddRange(result.Review.Select(r => new ReviewItem
            {
                Kind = r.Kind, Detail = r.Detail, Sources = string.Join(",", r.Sources), CreatedAt = input.Now,
            }));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
        return result;
    }
}

public static class InventoryReports
{
    /// <summary>A source counts as available only after at least one successful collection (never "zero" for a source that never ran).</summary>
    public static async Task<SourceAvailability> AvailabilityAsync(NexusDbContext db, CancellationToken cancellationToken)
    {
        var ok = (await db.Jobs.AsNoTracking().Where(j => j.LastSuccessAt != null).Select(j => j.Name).ToListAsync(cancellationToken)).ToHashSet();
        return new SourceAvailability(ok.Contains("sccm.devices"), ok.Contains("intune.devices"), ok.Contains("entra.devices"), ok.Contains("ad.computers"),
            ok.Contains("intune.policies"), ok.Contains("intune.mam"), ok.Contains("entra.users"), ok.Contains("xdr.endpoints"));
    }

    public static async Task<KpiReport> BuildAsync(NexusDbContext db, CancellationToken cancellationToken) =>
        KpiCalculator.Calculate(await db.Assets.AsNoTracking().ToListAsync(cancellationToken), await AvailabilityAsync(db, cancellationToken));
}
