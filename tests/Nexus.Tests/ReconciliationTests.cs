using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nexus.Core;
using Nexus.Core.Configuration;
using Nexus.Data.Entities;
using Nexus.Data.Support;
using Nexus.Reconciliation;
using Nexus.Simulation;
using Nexus.Worker.Collection;

namespace Nexus.Tests;

public class ReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AadA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SccmDeviceRecord Sccm(int id, string name, Guid? aad = null, string? serial = null, string maker = "Dell Inc.", string? smbios = null,
        bool client = true, bool obsolete = false, int daysAgo = 1) => new()
    {
        ResourceId = id, Name = name, Client = client, Active = client, Obsolete = obsolete, AadDeviceId = aad, Serial = serial, Manufacturer = maker,
        Model = "Latitude", SmbiosGuid = smbios, OperatingSystem = "Microsoft Windows 11 Enterprise", LastActiveAt = Now.AddDays(-daysAgo), ClientActiveStatus = 1,
    };

    private static IntuneDeviceRecord Intune(string id, string name, Guid? aad = null, string? serial = null, string agent = "mdm", string owner = "company",
        string os = "Windows", string maker = "Dell Inc.", int daysAgo = 1) => new()
    {
        Id = id, DeviceName = name, AzureAdDeviceId = aad, SerialNumber = serial, Manufacturer = maker, ManagementAgent = agent, OwnerType = owner,
        OperatingSystem = os, LastSyncAt = Now.AddDays(-daysAgo), UserPrincipalName = "u@x",
    };

    private static EntraDeviceRecord Entra(string id, Guid? deviceId, string name = "x") => new()
    {
        Id = id, DeviceId = deviceId, DisplayName = name, OperatingSystem = "Windows", LastSignInAt = Now.AddDays(-1), Ownership = "Company",
    };

    private static AdComputerRecord Ad(string name) => new()
    {
        ObjectGuid = Guid.NewGuid(), Name = name, OperatingSystem = "Windows 11", LastLogonTimestamp = Now.AddDays(-2), Enabled = true,
    };

    private static ReconcileResult Run(IEnumerable<SccmDeviceRecord>? sccm = null, IEnumerable<IntuneDeviceRecord>? intune = null,
        IEnumerable<EntraDeviceRecord>? entra = null, IEnumerable<AdComputerRecord>? ad = null, IEnumerable<AssetLink>? previous = null) =>
        Reconciler.Run(new ReconcileInput((sccm ?? []).ToList(), (ad ?? []).ToList(), (intune ?? []).ToList(), (entra ?? []).ToList(),
            (previous ?? []).ToList(), Now, TimeSpan.FromDays(30)));

    [Fact]
    public void AadDeviceIdJoinsSccmIntuneAndEntraEvenWhenNamesDiffer()
    {
        var result = Run([Sccm(1, "OLD-NAME", AadA)], [Intune("i1", "NEW-NAME", AadA)], [Entra("e1", AadA)]);
        var asset = Assert.Single(result.Assets);
        Assert.True(asset is { InSccm: true, InIntune: true, InEntra: true });
        Assert.Equal("Both", asset.Coverage);
        Assert.Equal("High", asset.Confidence);
    }

    [Fact]
    public void SameNameWithoutStrongEvidenceNeverJoinsManagementRecords()
    {
        var result = Run([Sccm(1, "PC-1", serial: "SN-A1234")], [Intune("i1", "PC-1", serial: "SN-B9999")]);
        Assert.Equal(2, result.Assets.Count);
    }

    [Fact]
    public void ValidSerialJoinsWithMediumConfidence()
    {
        var result = Run([Sccm(1, "PC-1", serial: "ABC12345")], [Intune("i1", "PC-1X", serial: "abc12345")]);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("Medium", asset.Confidence);
        Assert.Contains(result.Links, l => l.Evidence == "serial");
    }

    [Theory]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("Default string")]
    [InlineData("System Serial Number")]
    [InlineData("0000000000")]
    [InlineData("None")]
    [InlineData("")]
    public void InvalidSerialsAreNeverIdentifiers(string serial)
    {
        Assert.Null(Reconciler.ValidSerial(serial));
        var result = Run([Sccm(1, "A", serial: serial), Sccm(2, "B", serial: serial)], [Intune("i1", "C", serial: serial)]);
        Assert.Equal(3, result.Assets.Count);
        Assert.Empty(result.Review);
    }

    [Fact]
    public void EmptyGuidIsNotAnIdentifier()
    {
        var result = Run([Sccm(1, "A", Guid.Empty), Sccm(2, "B", Guid.Empty)]);
        Assert.Equal(2, result.Assets.Count);
    }

    [Fact]
    public void DuplicateSerialAcrossTwoSccmDevicesGoesToReviewWithoutMerging()
    {
        var result = Run([Sccm(1, "A", serial: "SAME1234"), Sccm(2, "B", serial: "SAME1234")]);
        Assert.Equal(2, result.Assets.Count);
        Assert.Contains(result.Review, r => r.Kind == "DuplicateSerial");
        Assert.All(result.Assets, a => Assert.True(a.NeedsReview));
    }

    [Fact]
    public void SerialWithDifferentManufacturerIsNotMerged()
    {
        var result = Run([Sccm(1, "A", serial: "SAME1234", maker: "Dell Inc.")], [Intune("i1", "A", serial: "SAME1234", maker: "LENOVO")]);
        Assert.Equal(2, result.Assets.Count);
        Assert.Contains(result.Review, r => r.Kind == "SerialHardwareMismatch");
    }

    [Fact]
    public void ClonedVirtualMachinesSharingHardwareUuidAreNotMerged()
    {
        var result = Run([Sccm(1, "VM1", smbios: "SMBIOS-ABCD1234"), Sccm(2, "VM2", smbios: "SMBIOS-ABCD1234")]);
        Assert.Equal(2, result.Assets.Count);
        Assert.Contains(result.Review, r => r.Kind == "CloneSuspect");
    }

    [Fact]
    public void ObsoleteRecordWithSameUuidAsOneLiveRecordIsMerged()
    {
        var result = Run([Sccm(1, "PC", smbios: "SMBIOS-ABCD1234", obsolete: true), Sccm(2, "PC-NEW", smbios: "SMBIOS-ABCD1234")]);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("PC-NEW", asset.Name);
        Assert.Equal("Medium", asset.Confidence);
    }

    [Fact]
    public void TenantAttachIsNotMdm()
    {
        var result = Run([Sccm(1, "PC", AadA)], [Intune("i1", "PC", AadA, agent: "configurationManagerClient")]);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("TenantAttach", asset.IntuneChannel);
        Assert.Equal("OnlySccm", asset.Coverage);
    }

    [Theory]
    [InlineData("mdm", "Mdm")]
    [InlineData("configurationManagerClientMdm", "Mdm")]
    [InlineData("configurationManagerClient", "TenantAttach")]
    [InlineData("msSense", "SecurityManagement")]
    [InlineData("eas", "Other")]
    public void ManagementAgentMapsToChannel(string agent, string channel) => Assert.Equal(channel, Reconciler.ChannelOf(agent));

    [Fact]
    public void EntraRegisteredOnlyIsNotIntuneEnrolled()
    {
        var asset = Assert.Single(Run(entra: [Entra("e1", AadA)]).Assets);
        Assert.True(asset is { InEntra: true, InIntune: false });
        Assert.Equal("Neither", asset.Coverage);
    }

    [Fact]
    public void ReenrollmentCountsAsOneDeviceAndRaisesReview()
    {
        var result = Run([Sccm(1, "PC", AadA, serial: "ABC12345")],
            [Intune("old", "OLD-PC", Guid.NewGuid(), "ABC12345", daysAgo: 120), Intune("new", "PC", AadA, "ABC12345")]);
        var asset = Assert.Single(result.Assets);
        Assert.True(asset.NeedsReview);
        Assert.Contains(result.Review, r => r.Kind == "MultipleIntuneRecords");
    }

    [Fact]
    public void AdAttachesByNameOnlyAsLowConfidenceSupport()
    {
        var result = Run([Sccm(1, "PC-1", AadA)], ad: [Ad("pc-1")]);
        var asset = Assert.Single(result.Assets);
        Assert.True(asset is { InAd: true, AdByNameOnly: true });
        Assert.Equal("High", asset.Confidence); // supporting evidence does not weaken the management join
        Assert.Contains(result.Links, l => l is { Source: "ad", Confidence: "Low", Evidence: "name" });
    }

    [Fact]
    public void AmbiguousAdNameIsNotAttached()
    {
        var result = Run([Sccm(1, "PC-1", AadA), Sccm(2, "pc-1", Guid.NewGuid())], ad: [Ad("PC-1")]);
        Assert.Equal(3, result.Assets.Count);
        Assert.Contains(result.Review, r => r.Kind == "AmbiguousName");
    }

    [Fact]
    public void ByodAndCorporateOwnershipComeFromTheSource()
    {
        var result = Run(intune: [Intune("i1", "BYOD", Guid.NewGuid(), owner: "personal", os: "Android"), Intune("i2", "CORP", Guid.NewGuid(), os: "iOS")]);
        var byod = result.Assets.Single(a => a.Name == "BYOD");
        Assert.Equal(("Personal", "intune", "Android"), (byod.Ownership, byod.OwnershipSource, byod.Platform));
        Assert.Equal("Corporate", result.Assets.Single(a => a.Name == "CORP").Ownership);
    }

    [Fact]
    public void SccmDiscoveredWithoutClientIsNotCoveredAndHealthIsNoClient()
    {
        var asset = Assert.Single(Run([Sccm(1, "PC", client: false)]).Assets);
        Assert.Equal("NoClient", asset.SccmHealth);
        Assert.Equal("Neither", asset.Coverage);
    }

    [Fact]
    public void AssetIdsStayStableBetweenRuns()
    {
        var sccm = new[] { Sccm(1, "PC", AadA) };
        var intune = new[] { Intune("i1", "PC", AadA) };
        var first = Run(sccm, intune);
        var second = Run(sccm, intune, previous: first.Links);
        Assert.Equal(first.Assets[0].Id, second.Assets[0].Id);

        // The device is renamed and its SCCM record replaced: the Intune link keeps the identity.
        var third = Run([Sccm(9, "PC-RENAMED", AadA)], intune, previous: first.Links);
        Assert.Equal(first.Assets[0].Id, third.Assets[0].Id);
    }

    [Fact]
    public void RecencyWindowSeparatesStaleFromActive()
    {
        var result = Run([Sccm(1, "FRESH", daysAgo: 2), Sccm(2, "OLD", daysAgo: 90)]);
        Assert.True(result.Assets.Single(a => a.Name == "FRESH").IsActive);
        Assert.False(result.Assets.Single(a => a.Name == "OLD").IsActive);
    }
}

public class KpiCalculatorTests
{
    private static Asset A(string platform, string ownership, string coverage, bool active = true, string health = "Healthy", string channel = "None") => new()
    {
        Id = Guid.NewGuid(), Platform = platform, Ownership = ownership, Coverage = coverage, IsActive = active, SccmHealth = health, IntuneChannel = channel,
        InSccm = coverage is "Both" or "OnlySccm", SccmClient = coverage is "Both" or "OnlySccm", InIntune = channel != "None",
    };

    [Fact]
    public void CoverageUsesExplicitDenominatorsAndExcludesStaleAndPersonal()
    {
        var assets = new[]
        {
            A("WindowsClient", "Corporate", "Both", channel: "Mdm"),
            A("WindowsClient", "Corporate", "OnlySccm"),
            A("WindowsClient", "Corporate", "OnlyIntune", channel: "Mdm"),
            A("WindowsClient", "Corporate", "Both", active: false, channel: "Mdm"),   // stale: out of the percentage
            A("WindowsClient", "Personal", "OnlyIntune", channel: "Mdm"),             // BYOD: out
            A("WindowsServer", "Corporate", "OnlySccm"),
        };
        var report = KpiCalculator.Calculate(assets, SourceAvailability.All);
        var sccm = report.Kpis.Single(k => k.Key == "sccm-coverage");
        Assert.Equal((3, 4), (sccm.Numerator, sccm.Denominator)); // 2 clients + 1 server of 4 eligible
        var mdm = report.Kpis.Single(k => k.Key == "intune-mdm-coverage");
        Assert.Equal((2, 3), (mdm.Numerator, mdm.Denominator));   // servers are not required to use MDM
        var complete = report.Kpis.Single(k => k.Key == "complete-coverage");
        Assert.Equal((1, 3), (complete.Numerator, complete.Denominator));
        Assert.Equal(1, report.Counts.Stale);
        Assert.Equal(1, report.Counts.PersonalDevices);
    }

    [Fact]
    public void UnavailableSourceIsNotEnabledNeverZeroPercent()
    {
        var report = KpiCalculator.Calculate([A("WindowsClient", "Corporate", "OnlySccm")], new SourceAvailability(true, false, false, true));
        var mdm = report.Kpis.Single(k => k.Key == "intune-mdm-coverage");
        Assert.Equal(KpiState.NotEnabled, mdm.State);
        Assert.Null(mdm.Percent);
        Assert.Equal(KpiState.Available, report.Kpis.Single(k => k.Key == "sccm-coverage").State);
    }

    [Fact]
    public void EmptyPopulationIsNoData()
    {
        var report = KpiCalculator.Calculate([], SourceAvailability.All);
        Assert.All(report.Kpis, k => Assert.Equal(KpiState.NoData, k.State));
    }
}

public class InventoryPipelineTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
    private readonly SyntheticEstate _estate = new();
    private readonly FakeTimeProvider _clock = new(SyntheticEstate.Now.AddMinutes(30));
    private readonly SwitchableSources _sources;
    private readonly JobRunner _runner;

    public InventoryPipelineTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var paths = new NexusPaths(_dataDir);
        var settings = new NexusSettings();
        settings.Sccm.Mode = SourceMode.Simulated;
        settings.ActiveDirectory.Mode = SourceMode.Simulated;
        new SettingsStore(paths).Save(settings);
        _sources = new SwitchableSources(_estate);
        _runner = new JobRunner(_db, new SettingsProvider(paths), _sources, new CollectionGate(new FixedLoad(10), _clock), _clock, NullLogger<JobRunner>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, true);
        }
    }

    private async Task CollectAllAsync()
    {
        foreach (var job in JobNames.Collections)
        {
            Assert.Equal(JobRunner.StatusSucceeded, (await _runner.RunAsync(job, default)).Status);
        }
    }

    [Fact]
    public async Task FullPipelineBuildsAssetsAndHandlesTheSyntheticEdgeCases()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        var assets = await db.Assets.ToListAsync();
        var links = (await db.AssetLinks.ToListAsync()).Where(l => l.Source != "mam").ToList(); // MAM links are evidence, not source records

        Assert.True(assets.Count > 400);
        Assert.Equal(links.Select(l => (l.Source, l.SourceKey)).Distinct().Count(), links.Count); // every record is in exactly one asset
        Assert.Equal(_estate.SccmSystems.Count + _estate.IntuneDevices.Count + _estate.EntraDevices.Count + _estate.AdComputers.Count, links.Count);

        // Renamed device + obsolete record collapse into one asset.
        Assert.Single(assets, a => a.Name.EndsWith("-NOVO"));
        // Cloned VMs stay apart and are flagged.
        Assert.Equal(2, assets.Count(a => a.Name.StartsWith("AZ-VM-CLONE")));
        Assert.Contains(await db.ReviewItems.ToListAsync(), r => r.Kind == "CloneSuspect");
        // 100 mobile devices: 40 corporate and 60 personal.
        Assert.Equal(60, assets.Count(a => a.Platform is "Android" or "iOS" && a.Ownership == "Personal" && a.InIntune));
        Assert.Equal(40, assets.Count(a => a.Platform is "Android" or "iOS" && a.Ownership == "Corporate" && a.InIntune));
        // Tenant attach exists and is not counted as MDM.
        Assert.Contains(assets, a => a.IntuneChannel == "TenantAttach" && a.Coverage != "Both" && a.Coverage != "OnlyIntune");
        // Job states exist for every source and the reconcile job ran.
        var jobs = await db.Jobs.ToDictionaryAsync(j => j.Name);
        Assert.All(JobNames.All, name => Assert.NotNull(jobs[name].LastSuccessAt));
    }

    [Fact]
    public async Task ReconcilingTwiceKeepsAssetIdsAndCounts()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        var before = (await db.Assets.Select(a => a.Id).ToListAsync()).Order().ToList();
        await new InventoryReconciler(_db, _clock).RunAsync(default);
        var after = (await db.Assets.AsNoTracking().Select(a => a.Id).ToListAsync()).Order().ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task GraphFailureKeepsLastSnapshotAndDoesNotChangeAssets()
    {
        await CollectAllAsync();
        await using var db = _db.Create();
        var intuneBefore = await db.IntuneDevices.CountAsync();
        var assetsBefore = await db.Assets.CountAsync();

        _sources.FailGraph = true;
        var outcome = await _runner.RunAsync(JobNames.Intune, default);
        Assert.Equal(JobRunner.StatusFailed, outcome.Status);
        Assert.Equal(intuneBefore, await db.IntuneDevices.CountAsync());
        Assert.Equal(assetsBefore, await db.Assets.CountAsync());
    }

    [Fact]
    public async Task UnconfiguredAzureIsNotConfiguredNotZero()
    {
        _sources.GraphEnabled = false;
        var outcome = await _runner.RunAsync(JobNames.Intune, default);
        Assert.Equal(JobRunner.StatusNotConfigured, outcome.Status);
        await using var db = _db.Create();
        Assert.Equal(0, await db.IntuneDevices.CountAsync());
    }
}
