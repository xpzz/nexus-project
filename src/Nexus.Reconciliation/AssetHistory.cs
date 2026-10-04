using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

/// <summary>
/// Change history of consolidated assets and the timeline of each tool's last-report dates (ADR-0007). Assets keep their internal id across
/// reconciliations (it follows the source links), so the same id can be followed over time.
/// </summary>
public static class AssetHistory
{
    private static readonly (string Field, Func<Asset, string?> Read)[] Tracked =
    [
        ("Name", a => a.Name),
        ("Serial", a => a.Serial),
        ("AssetType", a => a.AssetType),
        ("OperationalState", a => a.OperationalState),
        ("Ownership", a => a.Ownership),
        ("Coverage", a => a.Coverage),
        ("PrimaryUser", a => a.PrimaryUser),
        ("Department", a => a.Department),
        ("Model", a => a.Model),
        ("OperatingSystem", a => a.OperatingSystem),
        ("OsVersion", a => a.OsVersion),
        ("IntuneChannel", a => a.IntuneChannel),
        ("ComplianceState", a => a.ComplianceState),
        ("HasMam", a => a.HasMam ? "sim" : "não"),
        ("Confidence", a => a.Confidence),
    ];

    private static readonly (string Source, Func<Asset, DateTimeOffset?> At)[] Signals =
    [
        ("sccm", a => a.SccmLastSeenAt), ("intune", a => a.IntuneLastSyncAt), ("xdr", a => a.XdrLastSeenAt), ("netskope", a => a.NetskopeLastSeenAt),
        ("mam", a => a.MamLastSyncAt), ("entra", a => a.EntraLastSignInAt), ("ad", a => a.AdLastLogonAt),
    ];

    private static string? Clip(string? value) => value is { Length: > 500 } v ? v[..500] : value;

    /// <summary>Differences between the previous and the new set of assets. The very first load (nothing before) records no changes.</summary>
    public static IReadOnlyList<AssetChange> Diff(IReadOnlyDictionary<Guid, Asset> before, IReadOnlyList<Asset> after, DateTimeOffset now, Guid runId)
    {
        var changes = new List<AssetChange>();
        if (before.Count == 0)
        {
            return changes;
        }

        var seen = new HashSet<Guid>();
        foreach (var asset in after)
        {
            seen.Add(asset.Id);
            if (!before.TryGetValue(asset.Id, out var old))
            {
                changes.Add(new AssetChange { AssetId = asset.Id, AssetName = asset.Name, At = now, Field = "Created", NewValue = Clip(asset.Name), RunId = runId });
                continue;
            }

            foreach (var (field, read) in Tracked)
            {
                var (was, is_) = (read(old), read(asset));
                if (!string.Equals(was, is_, StringComparison.Ordinal))
                {
                    changes.Add(new AssetChange { AssetId = asset.Id, AssetName = asset.Name, At = now, Field = field, OldValue = Clip(was), NewValue = Clip(is_), RunId = runId });
                }
            }
        }

        foreach (var (id, old) in before.Where(b => !seen.Contains(b.Key)))
        {
            changes.Add(new AssetChange { AssetId = id, AssetName = old.Name, At = now, Field = "Removed", OldValue = Clip(old.Name), RunId = runId });
        }

        return changes;
    }

    /// <summary>
    /// One entry per source and asset when the tool's last-report date moved since the previous reconciliation (new assets always get theirs).
    /// With <paramref name="seed"/> every known date is written once: the first run after the timeline table appeared.
    /// </summary>
    public static IReadOnlyList<EvidenceTimelineEntry> Timeline(IReadOnlyDictionary<Guid, Asset> before, IReadOnlyList<Asset> assets, DateTimeOffset now, bool seed = false)
    {
        var entries = new List<EvidenceTimelineEntry>();
        foreach (var asset in assets)
        {
            before.TryGetValue(asset.Id, out var old);
            foreach (var (source, read) in Signals)
            {
                if (read(asset) is not { } at)
                {
                    continue;
                }

                var was = old is null ? null : read(old);
                if (seed || was is null || at - was.Value > TimeSpan.FromHours(12))
                {
                    entries.Add(new EvidenceTimelineEntry { AssetId = asset.Id, Source = source, ObservedAt = at, CollectedAt = now });
                }
            }
        }

        return entries;
    }
}
