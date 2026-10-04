using System.Globalization;

namespace Nexus.Collectors.Graph;

/// <summary>
/// Identifier hygiene before anything goes into a Graph URL or a <c>$batch</c> request. An empty id makes the whole batch fail with HTTP 400,
/// a zero GUID is a placeholder some sources emit for "no value", and a duplicate request id is rejected by the service.
/// </summary>
public static class GraphIds
{
    /// <summary>True for a non-empty GUID in any common format; false for null, blank, malformed and the all-zero GUID.</summary>
    public static bool IsUsable(string? id) => TryNormalize(id, out _);

    public static bool TryNormalize(string? id, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id.Trim(), out var guid) || guid == Guid.Empty)
        {
            return false;
        }

        normalized = guid.ToString("D", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Usable ids in lower-case D format, without duplicates, in first-seen order.</summary>
    public static List<string> Clean(IEnumerable<string?> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var id in ids)
        {
            if (TryNormalize(id, out var normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
