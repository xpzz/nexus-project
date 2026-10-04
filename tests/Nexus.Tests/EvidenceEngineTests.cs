using Nexus.Core.Configuration;
using Nexus.Data.Entities;
using Nexus.Reconciliation;

namespace Nexus.Tests;

public class EvidenceEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly EvidencePolicy Policy = EvidencePolicy.From(new EvidenceSettings());

    private static Asset Win(Action<Asset>? set = null)
    {
        var a = new Asset { Name = "AZ-NB-00001", Platform = "WindowsClient", Ownership = "Corporate", OperatingSystem = "Microsoft Windows 11 Enterprise" };
        set?.Invoke(a);
        return a;
    }

    private static EvidenceResult Eval(Action<Asset> set) => EvidenceEngine.Evaluate(Win(set), Now, Policy);

    [Fact]
    public void TwoIndependentToolsInsideTheConfirmedWindowConfirmTheAsset()
    {
        var r = Eval(a => { a.SccmLastSeenAt = Now.AddDays(-2); a.IntuneLastSyncAt = Now.AddHours(-8); a.EntraLastSignInAt = Now.AddDays(-1); });
        Assert.Equal(OperationalStates.Confirmed, r.State);
        Assert.True(r.Score >= 90, r.Explanation);
        Assert.Contains("Ativo confirmado, score", r.Explanation);
        Assert.Contains("SCCM (contato do cliente) há 2 dias", r.Explanation);
        Assert.Contains("Intune (sincronização) há 8 horas", r.Explanation);
        Assert.Contains("Entra ID (login) há 1 dia", r.Explanation);
    }

    [Fact]
    public void OneToolAloneIsOnlyProbable()
    {
        var r = Eval(a => a.IntuneLastSyncAt = Now.AddDays(-3));
        Assert.Equal(OperationalStates.Probable, r.State);
        Assert.True(r.IsActive);
    }

    [Fact]
    public void TwoToolsOutsideTheConfirmedWindowAreProbable() =>
        Assert.Equal(OperationalStates.Probable, Eval(a => { a.SccmLastSeenAt = Now.AddDays(-20); a.IntuneLastSyncAt = Now.AddDays(-22); }).State);

    [Fact]
    public void IdentitySignalsAloneNeverConfirm()
    {
        Assert.Equal(OperationalStates.NoRecent, Eval(a => a.AdLastLogonAt = Now.AddDays(-3)).State);
        Assert.Equal(OperationalStates.Probable, Eval(a => { a.AdLastLogonAt = Now.AddDays(-3); a.EntraLastSignInAt = Now.AddDays(-4); }).State);
        Assert.NotEqual(OperationalStates.Confirmed, Eval(a => { a.AdLastLogonAt = Now.AddDays(-1); a.EntraLastSignInAt = Now.AddDays(-1); }).State);
    }

    [Theory]
    [InlineData(60, OperationalStates.NoRecent, false)]
    [InlineData(100, OperationalStates.Inactive, false)]
    [InlineData(200, OperationalStates.Inactive, true)]
    public void OldReportsFallBackStepByStep(int days, string state, bool candidate)
    {
        var r = Eval(a => a.SccmLastSeenAt = Now.AddDays(-days));
        Assert.Equal(state, r.State);
        Assert.Equal(candidate, r.DecommissionCandidate);
        Assert.False(r.IsActive);
    }

    [Fact]
    public void NoDatesMeansUnknown()
    {
        var r = Eval(_ => { });
        Assert.Equal(OperationalStates.Unknown, r.State);
        Assert.Equal(0, r.Score);
        Assert.Contains("nenhuma fonte trouxe data", r.Explanation);
    }

    [Fact]
    public void IdentityConflictOverridesTheStateButKeepsTheActivityLevel()
    {
        var r = Eval(a => { a.SccmLastSeenAt = Now.AddDays(-1); a.IntuneLastSyncAt = Now.AddDays(-1); a.NeedsReview = true; });
        Assert.Equal(OperationalStates.Conflicting, r.State);
        Assert.Equal(OperationalStates.Confirmed, r.ActivityLevel);
        Assert.True(r.IsActive);
        Assert.Contains("conflito", r.Explanation);
    }

    [Fact]
    public void ObsoleteWithoutTelemetryIsDecommissionedButNotWhenToolsStillReport()
    {
        Assert.Equal(OperationalStates.Decommissioned, Eval(a => { a.SccmHealth = "Obsolete"; a.SccmLastSeenAt = Now.AddDays(-80); }).State);
        Assert.Equal(OperationalStates.Decommissioned, Eval(a => { a.InAd = true; a.AdEnabled = false; a.AdLastLogonAt = Now.AddDays(-5); }).State);
        Assert.Equal(OperationalStates.Probable, Eval(a => { a.SccmHealth = "Obsolete"; a.IntuneLastSyncAt = Now.AddDays(-2); }).State);
    }

    [Fact]
    public void ThresholdsComeFromThePolicyPerType()
    {
        Action<Asset> mobile = a => { a.Platform = "iOS"; a.Model = "iPhone 15"; a.IntuneLastSyncAt = Now.AddDays(-10); a.MamLastSyncAt = Now.AddDays(-12); };
        Assert.Equal(OperationalStates.Confirmed, Eval(mobile).State); // phones: confirmed window of 14 days
        Assert.Equal(OperationalStates.Probable, Eval(a => { a.IntuneLastSyncAt = Now.AddDays(-10); a.MamLastSyncAt = Now.AddDays(-12); }).State); // notebooks: 7 days
    }

    [Fact]
    public void ChangingTheSettingsChangesTheVerdict()
    {
        var settings = new EvidenceSettings();
        settings.Default.ProbableDays = 10;
        settings.Default.ConfirmedDays = 3;
        var a = Win(x => x.IntuneLastSyncAt = Now.AddDays(-15));
        Assert.Equal(OperationalStates.NoRecent, EvidenceEngine.Evaluate(a, Now, EvidencePolicy.From(settings)).State);
        Assert.Equal(OperationalStates.Probable, EvidenceEngine.Evaluate(a, Now, Policy).State);
    }

    [Fact]
    public void ThresholdsAreKeptInOrder()
    {
        var n = new EvidenceThresholds(30, 10, 5, 1).Normalized();
        Assert.True(n.ConfirmedDays <= n.ProbableDays && n.ProbableDays <= n.NoRecentDays && n.NoRecentDays <= n.DecommissionDays);
    }

    [Fact]
    public void ScoreCountsOnlyFreshSignals()
    {
        var fresh = Eval(a => a.IntuneLastSyncAt = Now.AddDays(-1));
        var withStale = Eval(a => { a.IntuneLastSyncAt = Now.AddDays(-1); a.AdLastLogonAt = Now.AddDays(-400); });
        Assert.Equal(fresh.Score, withStale.Score);
        Assert.Equal(80, fresh.Score);
    }

    [Theory]
    [InlineData("AZ-SRV-0001", "WindowsServer", null, null, AssetTypes.Server)]
    [InlineData("IPH-1", "iOS", "Apple", "iPhone 15", AssetTypes.Phone)]
    [InlineData("IPD-1", "iOS", "Apple", "iPad Air", AssetTypes.Tablet)]
    [InlineData("X-1", "Android", "Samsung", "Galaxy Tab S9", AssetTypes.Tablet)]
    [InlineData("MAC-1", "macOS", "Apple", "MacBook Pro", AssetTypes.Mac)]
    [InlineData("X-1", "WindowsClient", "Dell", "Latitude 5440", AssetTypes.Notebook)]
    [InlineData("X-1", "WindowsClient", "Dell", "OptiPlex 7010", AssetTypes.Desktop)]
    [InlineData("AZ-NB-00135", "WindowsClient", null, null, AssetTypes.Notebook)]
    [InlineData("AZ-DT-00135", "WindowsClient", null, null, AssetTypes.Desktop)]
    [InlineData("LOJA-KIOSK-01", "WindowsClient", null, null, AssetTypes.Kiosk)]
    [InlineData("AZ-0001", "WindowsClient", "Acme", "Model Z", AssetTypes.Unknown)]
    public void TypeComesFromHardwareThenFromNameTokens(string name, string platform, string? maker, string? model, string expected) =>
        Assert.Equal(expected, EvidenceEngine.TypeOf(new Asset { Name = name, Platform = platform, Manufacturer = maker, Model = model }, Policy));

    [Fact]
    public void NotebookTokenDoesNotMatchInsideOtherWords() =>
        Assert.Equal(AssetTypes.Unknown, EvidenceEngine.TypeOf(new Asset { Name = "BANCO-01", Platform = "WindowsClient" }, Policy));

    [Fact]
    public void TheLegacyPoolStaysConsistentWithTheEngine()
    {
        int?[] ages = [null, 1, 6, 15, 29, 45, 120];
        foreach (var sccm in ages)
        {
            foreach (var intune in ages)
            {
                foreach (var ad in ages)
                {
                    var a = Win(x =>
                    {
                        x.SccmLastSeenAt = sccm is null ? null : Now.AddDays(-sccm.Value);
                        x.IntuneLastSyncAt = intune is null ? null : Now.AddDays(-intune.Value);
                        x.AdLastLogonAt = ad is null ? null : Now.AddDays(-ad.Value);
                    });
                    var r = EvidenceEngine.Apply(a, Now, Policy);
                    Assert.True(a.IsActive == r.IsActive, $"sccm={sccm} intune={intune} ad={ad}: pool={a.IsActive}, engine={r.ActivityLevel}");
                }
            }
        }
    }

    [Fact]
    public void ApplyWritesTheVerdictAndTheSignalsIntoTheAsset()
    {
        var a = Win(x => { x.SccmLastSeenAt = Now.AddDays(-2); x.IntuneLastSyncAt = Now.AddDays(-1); });
        EvidenceEngine.Apply(a, Now, Policy);
        Assert.Equal(AssetTypes.Notebook, a.AssetType);
        Assert.Equal(OperationalStates.Confirmed, a.OperationalState);
        Assert.Equal(OperationalStates.Confirmed, a.ActivityLevel);
        Assert.NotNull(a.ActivityExplanation);
        Assert.Contains("\"k\":\"sccm\"", a.EvidenceJson);
        Assert.DoesNotContain("\"k\":\"xdr\"", a.EvidenceJson);
    }
}
