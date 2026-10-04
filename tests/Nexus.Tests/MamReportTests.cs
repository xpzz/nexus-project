using System.Text.Json;
using Nexus.Data.Entities;
using Nexus.Reconciliation;

namespace Nexus.Tests;

public class MamReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly EvidencePolicy Policy = EvidencePolicy.From(new Nexus.Core.Configuration.EvidenceSettings());
    private const string U1 = "0a0a0a0a-0000-0000-0000-000000000001", U2 = "0a0a0a0a-0000-0000-0000-000000000002", U3 = "0a0a0a0a-0000-0000-0000-000000000003", U4 = "0a0a0a0a-0000-0000-0000-000000000004";

    private static AssetView Phone(string platform, string user, bool mdm, bool mam, DateTimeOffset? access = null, string ownership = "Personal")
    {
        var a = new Asset
        {
            Id = Guid.NewGuid(), Name = platform + "-" + user[^1], Platform = platform, OperatingSystem = platform, Ownership = ownership, IntuneUserId = user, InIntune = true, IntuneLastSyncAt = Now.AddDays(-1),
            IntuneChannel = mdm ? "Mdm" : "None", HasMam = mam, MamLastSyncAt = mam ? Now.AddDays(-1) : null, LastM365AccessAt = access,
        };
        EvidenceEngine.Apply(a, Now, Policy);
        return AssetView.From(a, SourceAvailability.All);
    }

    private static MamRegistrationRecord Reg(string user, string platform, string app, string? applied) =>
        new() { Id = Guid.NewGuid().ToString(), UserId = user, DeviceType = platform, AppIdentifier = app, AppliedPolicies = applied, DeviceTag = "t" + user[^1], LastSyncAt = Now.AddDays(-1) };

    private static AppProtectionPolicyRecord IosPolicy(bool assigned = true, string name = "iOS - Proteção") => new()
    {
        Id = Guid.NewGuid().ToString(), Platform = "iOS", Name = name, IsAssigned = assigned, LastModifiedAt = Now.AddDays(-20), Assignments = "Grupo: BYOD",
        AppsJson = JsonSerializer.Serialize(new[] { "com.microsoft.office.outlook", "com.microsoft.msedge" }),
        SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["pinRequired"] = "true", ["dataBackupBlocked"] = "true", ["allowedOutboundClipboardSharingLevel"] = "managedApps" }),
    };

    private static InventorySnapshot Snapshot(IReadOnlyList<AssetView> views, ProtectionData data, SourceAvailability? src = null) =>
        new(views, [], [], src ?? SourceAvailability.All, Now, null, 30, Policy, data);

    private static ProtectionData Data(IEnumerable<AppProtectionPolicyRecord>? policies = null, IEnumerable<AppConfigRecord>? configs = null, IEnumerable<ConditionalAccessRecord>? ca = null,
        IEnumerable<AccessEvidenceRecord>? access = null, IEnumerable<MamRegistrationRecord>? regs = null, IEnumerable<ProtectionException>? exceptions = null) =>
        new(policies?.ToList() ?? [], configs?.ToList() ?? [], ca?.ToList() ?? [], access?.ToList() ?? [], regs?.ToList() ?? [], exceptions?.ToList() ?? [],
            [new() { Id = U1, UserPrincipalName = "ana@azul.corp" }, new() { Id = U2, UserPrincipalName = "bia@azul.corp" }], new GovernanceSettingsView(1000, 10, 180));

    [Fact]
    public void ControlsClaimProtectionOnlyWhenTheyReachDevices()
    {
        var applied = IosPolicy(name: "A");
        var unapplied = IosPolicy(name: "B");
        var notAssigned = IosPolicy(assigned: false, name: "C");
        var regs = new[] { Reg(U1, "iOS", "com.microsoft.office.outlook", "A"), Reg(U2, "iOS", "com.microsoft.teams", "A") };
        var report = Snapshot([], Data([applied, unapplied, notAssigned], regs: regs)).Mam();

        PolicyView P(string n) => report.Policies.Single(p => p.Policy.Name == n);
        Assert.Equal(2, P("A").AppliedRegistrations);
        Assert.All(P("A").Controls.Where(c => c.Result.Setting == ControlSetting.Configured), c => Assert.Equal(EvidenceLevel.EffectiveProven, c.Level));
        Assert.All(P("B").Controls.Where(c => c.Result.Setting == ControlSetting.Configured), c => Assert.Equal(EvidenceLevel.AssignedNoEvidence, c.Level));
        Assert.All(P("C").Controls.Where(c => c.Result.Setting == ControlSetting.Configured), c => Assert.Equal(EvidenceLevel.ConfiguredNotAssigned, c.Level));
        Assert.Equal(3, P("A").ConfiguredCount);
        Assert.Contains(P("A").Controls, c => c.Result.Key == "screenshot" && c.Level == EvidenceLevel.NotApplicable); // iOS has no such control
    }

    [Fact]
    public void WithoutMamCollectionAConfiguredAssignedControlIsOnlyInsufficientEvidence()
    {
        var src = new SourceAvailability(true, true, true, true, Mam: false);
        var report = Snapshot([], Data([IosPolicy()]), src).Mam();
        Assert.All(report.Policies[0].Controls.Where(c => c.Result.Setting == ControlSetting.Configured), c => Assert.Equal(EvidenceLevel.InsufficientData, c.Level));
    }

    [Fact]
    public void StalePoliciesAreFlagged()
    {
        var old = IosPolicy();
        old.LastModifiedAt = Now.AddDays(-400);
        Assert.True(Snapshot([], Data([old])).Mam().Policies[0].Stale);
        Assert.False(Snapshot([], Data([IosPolicy()])).Mam().Policies[0].Stale);
    }

    [Fact]
    public void UserCoverageUsesPeopleAsTheUnitAndSeparatesTheGaps()
    {
        var views = new[] { Phone("iOS", U1, true, true), Phone("Android", U2, false, false), Phone("iOS", U3, false, true) };
        var regs = new[]
        {
            Reg(U1, "iOS", "com.microsoft.office.outlook", "iOS - Proteção"), // covered
            Reg(U3, "iOS", "com.microsoft.teams", null),                      // registered, no policy applied
        };
        var access = new[] { new AccessEvidenceRecord { Key = "anon:u4", UserId = U4, OperatingSystem = "Android", LastAccessAt = Now.AddDays(-2) } }; // never seen anywhere else
        var ex = new[] { new ProtectionException { Id = 1, SubjectKind = "user", SubjectId = U2, SubjectName = "bia", Control = "mam", Reason = "Aguardando troca de aparelho", ApprovedBy = "x", Active = true } };
        var users = Snapshot(views, Data(regs: regs, access: access, exceptions: ex)).Mam().Users;

        Assert.Equal(4, users.Eligible);
        Assert.Equal(1, users.Covered);
        Assert.Equal(1, users.RegisteredWithoutPolicy);
        Assert.Equal(2, users.Uncovered);
        Assert.Equal(1, users.WithException);
        Assert.Equal(25.0, users.Percent);
        Assert.Equal(3, users.Gaps.Count);
        Assert.Equal(U2, users.Gaps.Last().UserId); // the excepted one goes last
        Assert.NotNull(users.Gaps.Last().Exception);
    }

    [Fact]
    public void ExpiredExceptionsDoNotHideAGap()
    {
        var views = new[] { Phone("Android", U2, false, false) };
        var expired = new[] { new ProtectionException { Id = 1, SubjectKind = "user", SubjectId = U2, Control = "mam", Reason = "x", ApprovedBy = "y", ExpiresAt = Now.AddDays(-1), Active = true } };
        var users = Snapshot(views, Data(exceptions: expired)).Mam().Users;
        Assert.Equal(0, users.WithException);
        Assert.Single(users.Gaps);
    }

    [Fact]
    public void AppInstancesAreCountedByRegistrationAndNeverMixedWithPeople()
    {
        var regs = new[] { Reg(U1, "iOS", "com.microsoft.teams", "P"), Reg(U1, "iOS", "com.microsoft.office.outlook", "P"), Reg(U2, "Android", "com.microsoft.teams", null) };
        var apps = Snapshot([], Data(regs: regs)).Mam().Apps;
        Assert.Equal((3, 2, 1), (apps.Total, apps.WithPolicy, apps.WithoutPolicy));
        Assert.Equal(66.7, apps.Percent);
        Assert.Equal(2, apps.ByPlatform["iOS"]);
        Assert.Equal(("com.microsoft.teams", 2, 1), apps.TopApps[0]);
    }

    [Fact]
    public void DevicesAreSplitByMdmAndMamAndByOwnership()
    {
        var views = new[]
        {
            Phone("iOS", U1, true, true), Phone("iOS", U2, true, false), Phone("Android", U3, false, true), Phone("Android", U4, false, false),
            Phone("iOS", U1, true, false, ownership: "Corporate"),
        };
        var devices = Snapshot(views, Data()).Mam().Devices;
        var byod = devices[0];
        Assert.Equal((1, 1, 1, 1), (byod.MdmAndMam, byod.MdmOnly, byod.MamOnly, byod.Neither));
        Assert.Equal(4, byod.Total);
        Assert.Equal(1, devices[1].MdmOnly);
    }

    [Fact]
    public void EdgeListsReportCapacityAndTheRegistrationsThatProveTheyReachDevices()
    {
        var cfg = new AppConfigRecord
        {
            Id = "c1", Kind = "managed-app", Platform = "iOS", Name = "Edge iOS",
            AppsJson = JsonSerializer.Serialize(new[] { "com.microsoft.msedge" }),
            SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["com.microsoft.intune.mam.managedbrowser.BlockListURLs"] = string.Join("|", Enumerable.Range(0, 930).Select(i => $"s{i}.com")), ["com.microsoft.intune.mam.managedbrowser.AllowListURLs"] = "corp.com" }),
        };
        var other = new AppConfigRecord { Id = "c2", Kind = "managed-app", Platform = "iOS", Name = "Outlook", AppsJson = "[\"com.microsoft.office.outlook\"]", SettingsJson = "{\"Theme\":\"dark\"}" };
        var regs = new[] { Reg(U1, "iOS", "com.microsoft.msedge", "iOS - Proteção"), Reg(U2, "iOS", "com.microsoft.msedge", null), Reg(U3, "Android", "com.microsoft.emmx", "x") };
        var edge = Assert.Single(Snapshot([], Data(configs: [cfg, other], regs: regs)).Mam().Edge);

        Assert.Equal(EdgeListInfo.Warning, edge.Lists.State);
        Assert.Equal((930, 1, 70), (edge.Lists.BlockCount, edge.Lists.AllowCount, edge.Lists.Left));
        Assert.Equal((2, 1), (edge.EdgeRegistrations, edge.EdgeRegistrationsWithPolicy)); // Android Edge does not count for an iOS configuration
    }

    private static ConditionalAccessRecord Ca(string state, bool app, bool m365 = true) => new() { Id = Guid.NewGuid().ToString(), Name = "ca", State = state, RequiresAppProtection = app, RequiresApprovedApp = app, TargetsMicrosoft365 = m365 };

    [Fact]
    public void ConditionalAccessRequirementIsEnforcedReportOnlyOrAbsent()
    {
        Assert.Equal(CaRequirementState.Enforced, Snapshot([], Data(ca: [Ca("enabled", true)])).Mam().ConditionalAccess.State);
        Assert.Equal(CaRequirementState.ReportOnly, Snapshot([], Data(ca: [Ca("enabledForReportingButNotEnforced", true)])).Mam().ConditionalAccess.State);
        Assert.Equal(CaRequirementState.Absent, Snapshot([], Data(ca: [Ca("disabled", true), Ca("enabled", false), Ca("enabled", true, m365: false)])).Mam().ConditionalAccess.State);
        Assert.Equal(CaRequirementState.Absent, Snapshot([], Data()).Mam().ConditionalAccess.State);
    }

    [Fact]
    public void AccessWithoutProofOfProtectionListsDevicesAndFaceslessMobileSignIns()
    {
        var views = new[]
        {
            Phone("iOS", U1, false, false, access: Now.AddDays(-1)),  // access, no MDM, no MAM
            Phone("iOS", U2, true, false, access: Now.AddDays(-1)),   // access but managed
            Phone("Android", U3, false, true, access: Now.AddDays(-1)), // access with MAM
        };
        var access = new[]
        {
            new AccessEvidenceRecord { Key = "anon:a", UserId = U4, OperatingSystem = "iOS", LastAccessAt = Now.AddDays(-1) },     // mobile, no device, user has no MAM
            new AccessEvidenceRecord { Key = "anon:b", UserId = U3, OperatingSystem = "Android", LastAccessAt = Now.AddDays(-1) }, // user has MAM registration: not a gap
            new AccessEvidenceRecord { Key = "anon:c", UserId = U4, OperatingSystem = "Windows", LastAccessAt = Now.AddDays(-1) }, // not mobile
        };
        var gaps = Snapshot(views, Data(access: access, regs: [Reg(U3, "Android", "com.microsoft.teams", "p")])).Mam().Access;
        Assert.Equal(1, gaps.DevicesWithoutProtection);
        Assert.Equal(1, gaps.MobileAccessWithoutDevice);
        Assert.Equal("anon:a", gaps.Anonymous.Single().Key);
    }

    [Fact]
    public void ByodPlatformsSeparateMdmFromMamAndFlagAccessWithoutProtection()
    {
        var views = new[]
        {
            Phone("Android", U1, true, true), Phone("Android", U2, true, false), Phone("Android", U3, false, false, access: Now.AddDays(-1)), Phone("iOS", U4, false, true),
        };
        var byod = Snapshot(views, Data()).Mam().Byod.ToDictionary(x => x.Platform);
        Assert.Equal((3, 2, 1, 1, 1, 1), (byod["Android"].Devices, byod["Android"].Mdm, byod["Android"].Mam, byod["Android"].MdmAndMam, byod["Android"].NoProtection, byod["Android"].WithAccessAndNoProtection));
        Assert.Equal((1, 0, 1), (byod["iOS"].Devices, byod["iOS"].Mdm, byod["iOS"].Mam));
    }

    [Fact]
    public void AnEmptyTenantProducesAnEmptyReportWithoutFailing()
    {
        var report = Snapshot([], ProtectionData.Empty).Mam();
        Assert.Equal(0, report.Users.Eligible);
        Assert.Null(report.Users.Percent);
        Assert.Null(report.Apps.Percent);
        Assert.Empty(report.Policies);
        Assert.Empty(report.Edge);
    }
}
