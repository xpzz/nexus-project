using Nexus.Reconciliation;

namespace Nexus.Tests;

public class ProtectionControlsTests
{
    private static Dictionary<string, string> Strong() => new()
    {
        ["allowedOutboundClipboardSharingLevel"] = "managedApps", ["allowedInboundDataTransferSources"] = "managedApps", ["allowedOutboundDataTransferDestinations"] = "managedApps",
        ["allowedDataStorageLocations"] = "oneDriveForBusiness,sharePoint", ["dataBackupBlocked"] = "true", ["pinRequired"] = "true", ["minimumPinLength"] = "6",
        ["appDataEncryptionType"] = "whenDeviceLocked", ["screenCaptureBlocked"] = "true", ["encryptAppData"] = "true", ["periodOfflineBeforeWipeIsEnforced"] = "P90D",
    };

    private static Dictionary<string, ControlResult> Eval(string platform, Dictionary<string, string> s) => ProtectionControls.Evaluate(platform, s).ToDictionary(c => c.Key);

    [Fact]
    public void AStrictIosPolicyConfiguresEverythingExceptWhatIosDoesNotOffer()
    {
        var r = Eval("iOS", Strong());
        Assert.All(r.Values.Where(c => c.Key != "screenshot"), c => Assert.Equal(ControlSetting.Configured, c.Setting));
        Assert.Equal(ControlSetting.NotApplicable, r["screenshot"].Setting);
        Assert.Contains("90 dia", r["wipe"].Value);
        Assert.Contains("mínimo 6", r["pin"].Value);
        Assert.Equal(ProtectionControls.All.Length, r.Count);
    }

    [Fact]
    public void AStrictAndroidPolicyUsesAndroidKeys()
    {
        var r = Eval("Android", Strong());
        Assert.All(r.Values, c => Assert.Equal(ControlSetting.Configured, c.Setting));
        Assert.Equal(ControlSetting.NotConfigured, Eval("Android", new() { ["screenCaptureBlocked"] = "false" })["screenshot"].Setting);
    }

    [Fact]
    public void ALoosePolicyConfiguresNothingAndSaysWhy()
    {
        var r = Eval("iOS", new()
        {
            ["allowedOutboundClipboardSharingLevel"] = "allowed", ["allowedInboundDataTransferSources"] = "allApps", ["allowedOutboundDataTransferDestinations"] = "allApps",
            ["allowedDataStorageLocations"] = "oneDriveForBusiness,localStorage,photoLibrary", ["appDataEncryptionType"] = "useDeviceSettings",
        });
        Assert.All(r.Values.Where(c => c.Key != "screenshot"), c => Assert.Equal(ControlSetting.NotConfigured, c.Setting));
        Assert.Contains("localStorage", r["storage"].Value);
        Assert.Contains("qualquer aplicativo", r["clipboard"].Value);
    }

    [Fact]
    public void MissingSettingsAreNotConfiguredNeverSilentlyProtected()
    {
        var r = Eval("Android", []);
        Assert.All(r.Values, c => Assert.Equal(ControlSetting.NotConfigured, c.Setting));
    }

    [Theory]
    [InlineData("managedApps", ControlSetting.Configured)]
    [InlineData("blocked", ControlSetting.Configured)]
    [InlineData("managedAppsWithPasteIn", ControlSetting.Configured)]
    [InlineData("allowed", ControlSetting.NotConfigured)]
    public void ClipboardLevels(string level, ControlSetting expected) =>
        Assert.Equal(expected, Eval("iOS", new() { ["allowedOutboundClipboardSharingLevel"] = level })["clipboard"].Setting);

    [Fact]
    public void SaveAsBlockedCountsAsCorporateOnlyStorage() =>
        Assert.Equal(ControlSetting.Configured, Eval("iOS", new() { ["saveAsBlocked"] = "true" })["storage"].Setting);

    [Theory]
    [InlineData(ControlSetting.NotApplicable, true, 5, true, EvidenceLevel.NotApplicable)]
    [InlineData(ControlSetting.NotConfigured, true, 5, true, EvidenceLevel.NotConfigured)]
    [InlineData(ControlSetting.Configured, false, 5, true, EvidenceLevel.ConfiguredNotAssigned)]
    [InlineData(ControlSetting.Configured, true, 5, false, EvidenceLevel.InsufficientData)]
    [InlineData(ControlSetting.Configured, true, 0, true, EvidenceLevel.AssignedNoEvidence)]
    [InlineData(ControlSetting.Configured, true, 3, true, EvidenceLevel.EffectiveProven)]
    public void AssignedPolicyIsNotProtectionWithoutEvidence(ControlSetting setting, bool assigned, int applied, bool collected, EvidenceLevel expected) =>
        Assert.Equal(expected, EvidenceLevels.Of(setting, assigned, applied, collected));

    [Fact]
    public void EveryEvidenceLevelHasATitleAndAnIcon()
    {
        foreach (var level in Enum.GetValues<EvidenceLevel>())
        {
            Assert.False(string.IsNullOrWhiteSpace(EvidenceLevels.Title(level)));
            Assert.False(string.IsNullOrWhiteSpace(EvidenceLevels.Icon(level)));
        }
    }

    [Theory]
    [InlineData("a.com|b.com|c.com", 3)]
    [InlineData("a.com;b.com;A.COM", 2)]
    [InlineData("[\"x.com\",\"y.com\"]", 2)]
    [InlineData("a.com\nb.com\r\nc.com", 3)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("[broken", 1)]
    public void EntriesAreSplitAndDeduplicated(string? value, int expected) => Assert.Equal(expected, EdgeUrlLists.Entries(value).Count);

    [Theory]
    [InlineData("URLBlocklist", true, false)]
    [InlineData("com.microsoft.intune.mam.managedbrowser.BlockListURLs", true, false)]
    [InlineData("BlockListURLs", true, false)]
    [InlineData("URLAllowlist", false, true)]
    [InlineData("com.microsoft.intune.mam.managedbrowser.AllowListURLs", false, true)]
    [InlineData("HomePage", false, false)]
    public void ListKeysAreRecognizedAcrossAndroidAndIos(string key, bool block, bool allow)
    {
        Assert.Equal(block, EdgeUrlLists.IsBlockKey(key));
        Assert.Equal(allow, EdgeUrlLists.IsAllowKey(key));
    }

    private static Dictionary<string, string> Block(int n) => new() { ["URLBlocklist"] = string.Join("|", Enumerable.Range(0, n).Select(i => $"site{i}.com")) };

    [Theory]
    [InlineData(100, EdgeListInfo.Ok)]
    [InlineData(899, EdgeListInfo.Ok)]
    [InlineData(900, EdgeListInfo.Warning)]
    [InlineData(999, EdgeListInfo.Warning)]
    [InlineData(1000, EdgeListInfo.Full)]
    [InlineData(1200, EdgeListInfo.Full)]
    public void BlockListKeepsAMarginBeforeTheOperationalLimit(int entries, string state)
    {
        var info = EdgeUrlLists.Analyze(Block(entries), 1000, 10);
        Assert.Equal(state, info.State);
        Assert.Equal(900, info.WarningAt);
        Assert.Equal(Math.Max(0, 1000 - entries), info.Left);
    }

    [Fact]
    public void WithoutABlockListTheStateSaysSoAndAllowEntriesAreCounted()
    {
        var info = EdgeUrlLists.Analyze(new Dictionary<string, string> { ["URLAllowlist"] = "corp.azul.com|intranet.azul.com" }, 1000, 10);
        Assert.Equal(EdgeListInfo.NoList, info.State);
        Assert.Equal(2, info.AllowCount);
        Assert.Equal(0, info.BlockCount);
    }

    [Fact]
    public void LimitAndReserveComeFromSettings()
    {
        var info = EdgeUrlLists.Analyze(Block(450), 500, 20);
        Assert.Equal(400, info.WarningAt);
        Assert.Equal(EdgeListInfo.Warning, info.State);
        Assert.True(EdgeUrlLists.LooksLikeEdgeSettings(Block(1)));
        Assert.False(EdgeUrlLists.LooksLikeEdgeSettings(new Dictionary<string, string> { ["HomePage"] = "x" }));
    }
}

public class SccmExtrasTests
{
    [Theory]
    [InlineData(9, "laptop")]
    [InlineData(10, "laptop")]
    [InlineData(31, "laptop")]
    [InlineData(30, "tablet")]
    [InlineData(3, "desktop")]
    [InlineData(35, "desktop")]
    [InlineData(23, "server")]
    [InlineData(28, "server")]
    [InlineData(99, "other")]
    public void ChassisCodesMapToKinds(int code, string kind) => Assert.Equal(kind, Nexus.Collectors.Sccm.ChassisKinds.FromCode(code));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OtherAndUnknownChassisSayNothing(int code) => Assert.Null(Nexus.Collectors.Sccm.ChassisKinds.FromCode(code));

    [Fact]
    public void ALaptopBeatsADockWhenSeveralEnclosuresAreReported()
    {
        Assert.Equal("laptop", Nexus.Collectors.Sccm.ChassisKinds.Pick([3, 10]));
        Assert.Equal("desktop", Nexus.Collectors.Sccm.ChassisKinds.Pick([1, 3]));
        Assert.Null(Nexus.Collectors.Sccm.ChassisKinds.Pick([1, 2]));
    }

    [Theory]
    [InlineData("a4:bb:cc:dd:ee:01", "A4:BB:CC:DD:EE:01")]
    [InlineData("A4-BB-CC-DD-EE-01", "A4:BB:CC:DD:EE:01")]
    [InlineData("A4BBCCDDEE01", "A4:BB:CC:DD:EE:01")]
    [InlineData("a4bb.ccdd.ee01", "A4:BB:CC:DD:EE:01")]
    [InlineData("zz:bb:cc:dd:ee:01", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MacAddressesAreNormalized(string? input, string? expected) => Assert.Equal(expected, Nexus.Collectors.Sccm.NetworkIds.NormalizeMac(input));

    [Theory]
    [InlineData("A4:BB:CC:DD:EE:01", true)]
    [InlineData("00:00:00:00:00:00", false)]
    [InlineData("FF:FF:FF:FF:FF:FF", false)]
    [InlineData("00:15:5D:01:02:03", false)] // Hyper-V
    [InlineData("00:50:56:01:02:03", false)] // VMware
    [InlineData("02:BB:CC:DD:EE:01", false)] // locally administered
    [InlineData("A5:BB:CC:DD:EE:01", false)] // multicast bit
    [InlineData("not a mac", false)]
    public void OnlyHardwareMacsIdentifyADevice(string mac, bool identifying) => Assert.Equal(identifying, Nexus.Collectors.Sccm.NetworkIds.IsIdentifying(mac));
}
