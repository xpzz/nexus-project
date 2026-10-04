using Nexus.Data.Entities;

namespace Nexus.Reconciliation;

/// <summary>One tool as seen against the whole inventory: who it knows, who reported in the window, how old its reports are.</summary>
public sealed record SourceStat(string Key, string Label, bool Enabled, int Known, int Reporting, int Silent, int OnlyHere, int? MedianAgeDays, IReadOnlyList<int> AgeBuckets, DateTimeOffset? LatestAt);

/// <summary>Devices two tools know in common, how many of them both reported in the window and how many have dates more than <see cref="SourceComparison.GapDays"/> apart.</summary>
public sealed record PairCell(string A, string B, int Both, int BothReporting, int DatesApart);

/// <summary>A combination of tools that see the same set of devices (for example SCCM + Intune + XDR).</summary>
public sealed record SourceCombo(IReadOnlyList<string> Keys, int Count, int Active);

public sealed record SourceComparisonReport(
    int WindowDays, int Total, IReadOnlyList<SourceStat> Sources, IReadOnlyList<PairCell> Pairs, IReadOnlyList<SourceCombo> Combos,
    int InAllEnabled, int EnabledCount)
{
    public static SourceComparisonReport Empty { get; } = new(30, 0, [], [], [], 0, 0);

    public PairCell? Pair(string a, string b) => Pairs.FirstOrDefault(p => (p.A == a && p.B == b) || (p.A == b && p.B == a));
}

/// <summary>
/// Puts SCCM, Active Directory, Intune, Cortex XDR and Netskope side by side (ADR-0004 decides who is the same device, ADR-0006 how
/// recent a report must be). Presence and dates only: nothing here changes how a device is classified.
/// </summary>
public static class SourceComparison
{
    public const int GapDays = 14;

    public static readonly string[] AgeLabels = ["Até 7 dias", "8 a 30 dias", "31 a 90 dias", "Mais de 90 dias", "Sem data"];

    private static readonly (string Key, string Label, Func<Asset, bool> Known, Func<Asset, DateTimeOffset?> At, Func<SourceAvailability, bool> Enabled)[] Defs =
    [
        ("sccm", "SCCM", a => a.InSccm, a => a.SccmLastSeenAt, x => x.Sccm),
        ("ad", "Active Directory", a => a.InAd, a => a.AdLastLogonAt, x => x.ActiveDirectory),
        ("intune", "Intune", a => a.InIntune, a => a.IntuneLastSyncAt, x => x.Intune),
        ("xdr", "Cortex XDR", a => a.InXdr, a => a.XdrLastSeenAt, x => x.Xdr),
        ("netskope", "Netskope", a => a.InNetskope, a => a.NetskopeLastSeenAt, x => x.Netskope),
    ];

    public static string Label(string key) => Defs.First(d => d.Key == key).Label;

    private static bool Known(Asset a, string key) => Defs.First(d => d.Key == key).Known(a);

    private static DateTimeOffset? At(Asset a, string key) => Defs.First(d => d.Key == key).At(a);

    /// <summary>
    /// Drilldown filters behind the comparison page, carried in the inventory's funnel parameter:
    /// <c>fonte:k</c> (tool knows it), <c>sem:k</c> (does not), <c>so:k</c> (only that tool), <c>par:a:b</c> (both know it),
    /// <c>dif:a:b</c> (both know it, dates more than <see cref="GapDays"/> apart) and <c>combo:a+b+c</c> (exactly that set of tools).
    /// </summary>
    public static bool Matches(Asset a, string spec)
    {
        var p = spec.Split(':');
        try
        {
            return p[0] switch
            {
                "fonte" => Known(a, p[1]),
                "sem" => !Known(a, p[1]),
                "so" => Known(a, p[1]) && Defs.Where(d => d.Key != p[1]).All(d => !d.Known(a)),
                "par" => Known(a, p[1]) && Known(a, p[2]),
                "dif" => Known(a, p[1]) && Known(a, p[2]) && At(a, p[1]) is { } x && At(a, p[2]) is { } y && Math.Abs((x - y).TotalDays) > GapDays,
                "combo" => string.Join("+", Defs.Where(d => d.Known(a)).Select(d => d.Key)) == p[1],
                _ => true,
            };
        }
        catch (InvalidOperationException)
        {
            return false; // unknown tool key in a hand-edited link: show nothing instead of everything
        }
    }

    public static string? SpecLabel(string spec)
    {
        var p = spec.Split(':');
        try
        {
            return p[0] switch
            {
                "fonte" => $"Conhecidos no {Label(p[1])}",
                "sem" => $"Fora do {Label(p[1])}",
                "so" => $"Só no {Label(p[1])}",
                "par" => $"Em {Label(p[1])} e {Label(p[2])}",
                "dif" => $"{Label(p[1])} e {Label(p[2])} com datas a mais de {GapDays} dias",
                "combo" => "Só em " + string.Join(" + ", p[1].Split('+').Select(Label)),
                _ => null,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Last report of each of the five tools for one device (null when the tool does not know it or has no date).</summary>
    public static IReadOnlyList<(string Key, string Label, DateTimeOffset? At)> Dates(Asset a) => Defs.Select(d => (d.Key, d.Label, d.Known(a) ? d.At(a) : null)).ToList();

    /// <summary>Days between the most recent and the oldest report among the tools that have a date (0 when fewer than two).</summary>
    public static int Spread(Asset a)
    {
        var seen = Dates(a).Where(d => d.At is not null).Select(d => d.At!.Value).ToList();
        return seen.Count < 2 ? 0 : (int)(seen.Max() - seen.Min()).TotalDays;
    }

    public static SourceComparisonReport Build(IReadOnlyList<AssetView> views, SourceAvailability availability, DateTimeOffset now, int windowDays)
    {
        if (views.Count == 0)
        {
            return SourceComparisonReport.Empty;
        }

        var window = TimeSpan.FromDays(windowDays);
        bool Reported(Func<Asset, DateTimeOffset?> at, Asset a) => at(a) is { } t && now - t <= window;

        var stats = new List<SourceStat>();
        foreach (var d in Defs)
        {
            var known = views.Where(v => d.Known(v.Asset)).Select(v => v.Asset).ToList();
            var ages = known.Where(a => d.At(a) is not null).Select(a => Math.Max(0, (int)(now - d.At(a)!.Value).TotalDays)).OrderBy(x => x).ToList();
            var buckets = new int[5];
            foreach (var a in known)
            {
                var at = d.At(a);
                var age = at is null ? -1 : Math.Max(0, (int)(now - at.Value).TotalDays);
                buckets[age < 0 ? 4 : age <= 7 ? 0 : age <= 30 ? 1 : age <= 90 ? 2 : 3]++;
            }

            var reporting = known.Count(a => Reported(d.At, a));
            var onlyHere = known.Count(a => Defs.Where(o => o.Key != d.Key).All(o => !o.Known(a)));
            stats.Add(new SourceStat(d.Key, d.Label, d.Enabled(availability), known.Count, reporting, known.Count - reporting, onlyHere,
                ages.Count == 0 ? null : ages[ages.Count / 2], buckets, known.Select(a => d.At(a)).Where(t => t is not null).DefaultIfEmpty(null).Max()));
        }

        var pairs = new List<PairCell>();
        for (var i = 0; i < Defs.Length; i++)
        {
            for (var j = i + 1; j < Defs.Length; j++)
            {
                var (a, b) = (Defs[i], Defs[j]);
                var both = views.Where(v => a.Known(v.Asset) && b.Known(v.Asset)).Select(v => v.Asset).ToList();
                pairs.Add(new PairCell(a.Key, b.Key, both.Count, both.Count(x => Reported(a.At, x) && Reported(b.At, x)),
                    both.Count(x => a.At(x) is { } ta && b.At(x) is { } tb && Math.Abs((ta - tb).TotalDays) > GapDays)));
            }
        }

        var combos = views.GroupBy(v => string.Join("+", Defs.Where(d => d.Known(v.Asset)).Select(d => d.Key)))
            .Where(g => g.Key.Length > 0)
            .Select(g => new SourceCombo(g.Key.Split('+'), g.Count(), g.Count(v => v.Asset.IsActive)))
            .OrderByDescending(c => c.Count).Take(12).ToList();

        var enabled = Defs.Where(d => d.Enabled(availability)).ToList();
        var inAll = enabled.Count == 0 ? 0 : views.Count(v => enabled.All(d => d.Known(v.Asset)));
        return new SourceComparisonReport(windowDays, views.Count, stats, pairs, combos, inAll, enabled.Count);
    }
}
