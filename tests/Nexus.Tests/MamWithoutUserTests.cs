using Nexus.Data.Entities;
using Nexus.Reconciliation;

namespace Nexus.Tests;

public class MamWithoutUserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static MamRegistrationRecord Reg(string id, string? user, string? tag, string platform = "iOS") => new()
    {
        Id = id, UserId = user, DeviceTag = tag, DeviceName = tag is null ? null : "Aparelho " + tag, DeviceType = platform, AppIdentifier = "com.microsoft.teams",
        LastSyncAt = Now.AddDays(-1), CollectedAt = Now,
    };

    private static ReconcileResult Run(params MamRegistrationRecord[] regs) =>
        Reconciler.Run(new ReconcileInput([], [], [], [], [], Now, TimeSpan.FromDays(30), Mam: regs));

    [Fact]
    public void RegistrationsWithoutAUsableUserBecomeMamOnlyAssetsInsteadOfBeingDropped()
    {
        var result = Run(
            Reg("r1", null, "tag-a"),
            Reg("r2", "", "tag-b"),
            Reg("r3", Guid.Empty.ToString(), "tag-c", "Android"),
            Reg("r4", Guid.NewGuid().ToString(), "tag-d"));

        Assert.Equal(4, result.Assets.Count);
        var orphans = result.Assets.Where(a => a.PrimaryUser is null).ToList();
        Assert.Equal(3, orphans.Count);
        Assert.All(orphans, a =>
        {
            Assert.True(a.HasMam);
            Assert.Equal("OnlyMam", a.Coverage);
            Assert.Equal("Low", a.Confidence);
            Assert.Equal("Personal", a.Ownership);
        });
        Assert.Contains(result.Review, r => r.Kind == "MamWithoutUser" && r.Detail.StartsWith("3 aparelho(s)"));
    }

    [Fact]
    public void RegistrationWithoutUserAndWithoutTagStillGetsAnAssetFromItsOwnId()
    {
        var result = Run(Reg("only-id", null, null));
        var asset = Assert.Single(result.Assets);
        Assert.Equal("only-id", asset.Name);
        Assert.Contains(result.Links, l => l.AssetId == asset.Id && l.SourceKey == "tag:only-id");
    }

    [Fact]
    public void AppsOfTheSameUnidentifiedDeviceCollapseIntoOneAsset()
    {
        var result = Run(Reg("r1", null, "tag-a"), Reg("r2", null, "tag-a"), Reg("r3", null, "tag-a"));
        var asset = Assert.Single(result.Assets);
        Assert.Equal(3, asset.MamAppCount);
    }

    [Fact]
    public void AssetIdsAreStableAcrossRuns()
    {
        var first = Run(Reg("r1", null, "tag-a"));
        var again = Reconciler.Run(new ReconcileInput([], [], [], [], first.Links, Now, TimeSpan.FromDays(30), Mam: [Reg("r1", null, "tag-a")]));
        Assert.Equal(first.Assets[0].Id, again.Assets[0].Id);
    }
}
