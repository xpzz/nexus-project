using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nexus.Data.Entities;

namespace Nexus.Data.Support;

/// <summary>
/// Keeps every distinct version of each source record (ADR-0007). Raw tables are replaced on each collection, so without this the evidence behind
/// a past classification would be lost. A new version is written only when something other than a "last seen" date changed.
/// </summary>
public static partial class RawRecordArchive
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    // Dates that move on every collection (last sync, last active, collected at). They are recorded in the evidence timeline instead.
    [GeneratedRegex("^(Last|Collected)", RegexOptions.IgnoreCase)]
    private static partial Regex VolatileName();

    public sealed record Result(int Created, int Changed, int Unchanged, int Removed);

    public static string Payload<T>(T row) => JsonSerializer.Serialize(row, Options);

    /// <summary>SHA-256 of the payload without the volatile dates, with properties in a stable order.</summary>
    public static string HashOf(string payloadJson)
    {
        var node = JsonNode.Parse(payloadJson) as JsonObject ?? new JsonObject();
        var stable = new StringBuilder();
        foreach (var property in node.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => !VolatileName().IsMatch(p.Key)))
        {
            stable.Append(property.Key).Append('=').Append(property.Value?.ToJsonString()).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable.ToString())));
    }

    public static async Task<Result> ArchiveAsync<T>(NexusDbContext db, string source, IReadOnlyList<T> rows, Func<T, string> key, DateTimeOffset now, Guid runId, CancellationToken cancellationToken)
    {
        var current = await db.RawRecordVersions.AsNoTracking().Where(v => v.Source == source && v.IsCurrent)
            .Select(v => new { v.Id, v.SourceKey, v.Hash }).ToListAsync(cancellationToken);
        var byKey = current.GroupBy(v => v.SourceKey).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Id).First());

        var seen = new HashSet<string>();
        var touch = new List<long>();
        var supersede = new List<long>();
        var fresh = new List<RawRecordVersion>();
        int created = 0, changed = 0;
        foreach (var row in rows)
        {
            var k = key(row);
            if (string.IsNullOrWhiteSpace(k) || !seen.Add(k))
            {
                continue; // a record without a key cannot be versioned; a duplicate key keeps the first
            }

            var payload = Payload(row);
            var hash = HashOf(payload);
            if (byKey.TryGetValue(k, out var existing))
            {
                if (existing.Hash == hash)
                {
                    touch.Add(existing.Id);
                    continue;
                }

                supersede.Add(existing.Id);
                changed++;
            }
            else
            {
                created++;
            }

            fresh.Add(new RawRecordVersion { Source = source, SourceKey = k, PayloadJson = payload, Hash = hash, FirstSeenAt = now, LastSeenAt = now, IsCurrent = true, RunId = runId });
        }

        var gone = current.Where(v => !seen.Contains(v.SourceKey)).Select(v => v.Id).ToList();

        foreach (var chunk in touch.Chunk(1000))
        {
            await db.RawRecordVersions.Where(v => chunk.Contains(v.Id)).ExecuteUpdateAsync(s => s.SetProperty(v => v.LastSeenAt, now), cancellationToken);
        }

        foreach (var chunk in supersede.Chunk(1000))
        {
            await db.RawRecordVersions.Where(v => chunk.Contains(v.Id)).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false), cancellationToken);
        }

        foreach (var chunk in gone.Chunk(1000))
        {
            await db.RawRecordVersions.Where(v => chunk.Contains(v.Id)).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false).SetProperty(v => v.RemovedAt, now), cancellationToken);
        }

        foreach (var chunk in fresh.Chunk(500))
        {
            db.RawRecordVersions.AddRange(chunk);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        return new Result(created, changed, touch.Count, gone.Count);
    }

    /// <summary>Drops superseded or removed versions older than the retention. Current versions are never deleted.</summary>
    public static async Task<int> PurgeAsync(NexusDbContext db, DateTimeOffset now, int retentionDays, CancellationToken cancellationToken)
    {
        var limit = now - TimeSpan.FromDays(Math.Max(30, retentionDays));
        return await db.RawRecordVersions.Where(v => !v.IsCurrent && v.LastSeenAt < limit).ExecuteDeleteAsync(cancellationToken);
    }
}
