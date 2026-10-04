using Nexus.Data.Entities;
using Nexus.Reconciliation;

namespace Nexus.Tests;

public class SourceComparisonTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static AssetView View(Action<Asset> set)
    {
        var a = new Asset { Name = "X", Platform = "WindowsClient", Ownership = "Corporate" };
        set(a);
        ActivityModel.Apply(a, Now, TimeSpan.FromDays(30));
        return AssetView.From(a, SourceAvailability.All);
    }

    private static readonly IReadOnlyList<AssetView> Estate =
    [
        View(a => { a.InSccm = true; a.SccmLastSeenAt = Now.AddDays(-2); a.InIntune = true; a.IntuneLastSyncAt = Now.AddDays(-1); a.InXdr = true; a.XdrLastSeenAt = Now.AddDays(-1); }),
        View(a => { a.InSccm = true; a.SccmLastSeenAt = Now.AddDays(-60); a.InIntune = true; a.IntuneLastSyncAt = Now.AddDays(-1); }),
        View(a => { a.InAd = true; a.AdLastLogonAt = Now.AddDays(-5); }),
        View(a => { a.InNetskope = true; a.NetskopeLastSeenAt = Now.AddDays(-100); }),
    ];

    private static SourceComparisonReport Report() => SourceComparison.Build(Estate, SourceAvailability.All, Now, 30);

    [Fact]
    public void CountsWhoKnowsAndWhoReportedInTheWindow()
    {
        var sccm = Report().Sources.Single(s => s.Key == "sccm");
        Assert.Equal(2, sccm.Known);
        Assert.Equal(1, sccm.Reporting);
        Assert.Equal(1, sccm.Silent);
        Assert.Equal(0, sccm.OnlyHere);
    }

    [Fact]
    public void OnlyHereCountsDevicesNoOtherToolKnows()
    {
        var r = Report();
        Assert.Equal(1, r.Sources.Single(s => s.Key == "ad").OnlyHere);
        Assert.Equal(1, r.Sources.Single(s => s.Key == "netskope").OnlyHere);
    }

    [Fact]
    public void AgeBucketsSplitByLastReport()
    {
        var netskope = Report().Sources.Single(s => s.Key == "netskope");
        Assert.Equal([0, 0, 0, 1, 0], netskope.AgeBuckets);
        Assert.Equal(100, netskope.MedianAgeDays);
    }

    [Fact]
    public void PairsCountSharedDevicesAndDatesApart()
    {
        var report = Report();
        var pair = report.Pair("sccm", "intune")!;
        Assert.Equal(2, pair.Both);
        Assert.Equal(1, pair.BothReporting);
        Assert.Equal(1, pair.DatesApart);
        Assert.Same(pair, report.Pair("intune", "sccm"));
    }

    [Fact]
    public void CombosListExactSetsOfTools()
    {
        var combo = Report().Combos.Single(c => c.Keys.SequenceEqual(["sccm", "intune", "xdr"]));
        Assert.Equal(1, combo.Count);
        Assert.Equal(1, combo.Active);
    }

    [Theory]
    [InlineData("fonte:sccm", 2)]
    [InlineData("sem:sccm", 2)]
    [InlineData("so:ad", 1)]
    [InlineData("par:sccm:intune", 2)]
    [InlineData("dif:sccm:intune", 1)]
    [InlineData("combo:sccm+intune", 1)]
    [InlineData("fonte:inexistente", 0)]
    public void DrilldownFiltersMatchTheReport(string spec, int expected) =>
        Assert.Equal(expected, Estate.Count(v => InventoryQuery.Funnel(v, spec, Now)));

    [Fact]
    public void LabelsAreNullForUnknownSpecs()
    {
        Assert.Equal("Só no Active Directory", SourceComparison.SpecLabel("so:ad"));
        Assert.Null(SourceComparison.SpecLabel("fonte:x"));
        Assert.Null(SourceComparison.SpecLabel("par:sccm"));
    }

    [Fact]
    public void EmptyInventoryGivesAnEmptyReport() => Assert.Equal(0, SourceComparison.Build([], SourceAvailability.All, Now, 30).Total);
}
