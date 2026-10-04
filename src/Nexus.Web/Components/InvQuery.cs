using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Nexus.Reconciliation;

namespace Nexus.Web.Components;

/// <summary>
/// The inventory page's state, kept entirely in the URL: filters, sort, columns and page. Every link is built by changing one value, so a filtered
/// view can be shared as it is and the browser's back button returns to the same filters, order and page.
/// </summary>
public sealed class InvQuery
{
    public static readonly string[] FilterKeys = ["q", "grupo", "gestao", "estado", "pendencia", "propriedade", "funil", "depto", "atividade", "oper", "tipo", "flag"];
    private static readonly string[] ViewKeys = ["ordem", "desc", "pagina", "cols", "tam"];
    private readonly SortedDictionary<string, string> _values;

    private InvQuery(SortedDictionary<string, string> values) => _values = values;

    public static InvQuery From(string? queryString) => FromPairs(QueryHelpers.ParseQuery(queryString ?? ""));

    public static InvQuery From(IQueryCollection query) => FromPairs(query);

    private static InvQuery FromPairs(IEnumerable<KeyValuePair<string, StringValues>> pairs)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            var k = key.ToLowerInvariant();
            if ((FilterKeys.Contains(k) || ViewKeys.Contains(k)) && value.ToString() is { Length: > 0 and <= 400 } v)
            {
                values[k] = v;
            }
        }

        return new InvQuery(values);
    }

    public string Get(string key) => _values.GetValueOrDefault(key, "");

    public bool Has(string key) => _values.ContainsKey(key);

    public int PageIndex => int.TryParse(Get("pagina"), out var p) && p > 0 ? p : 0;

    public bool Descending => Get("desc") == "true";

    public int PageSize => int.TryParse(Get("tam"), out var n) ? Math.Clamp(n, 10, 200) : 25;

    public IEnumerable<string> ActiveFilters => FilterKeys.Where(Has);

    /// <summary>A copy with one value changed (empty removes it). Changing a filter returns to the first page.</summary>
    public InvQuery With(string key, string? value)
    {
        var copy = new SortedDictionary<string, string>(_values, StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value))
        {
            copy.Remove(key);
        }
        else
        {
            copy[key] = value;
        }

        if (key != "pagina")
        {
            copy.Remove("pagina");
        }

        return new InvQuery(copy);
    }

    public InvQuery Without(string key) => With(key, null);

    public InvQuery OnlyViewKeys()
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in ViewKeys.Where(Has))
        {
            copy[k] = _values[k];
        }

        return new InvQuery(copy);
    }

    public string QueryString => string.Join("&", _values.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

    public string Url(string path = "/inventario") => _values.Count == 0 ? path : path + "?" + QueryString;

    public InventoryFilter ToFilter() => new(
        Group: Get("grupo").NullIfEmpty(), Management: Get("gestao").NullIfEmpty(), State: Get("estado").NullIfEmpty(), Issue: Get("pendencia").NullIfEmpty(),
        Ownership: Get("propriedade").NullIfEmpty(), Funnel: Get("funil").NullIfEmpty(), Query: Get("q").NullIfEmpty(), Department: Get("depto").NullIfEmpty(),
        Activity: Get("atividade").NullIfEmpty(), Oper: Get("oper").NullIfEmpty(), AssetType: Get("tipo").NullIfEmpty(), Flags: Get("flag").NullIfEmpty());
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string value) => value.Length == 0 ? null : value;
}
