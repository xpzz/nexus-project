using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nexus.Collectors.ActiveDirectory;
using Nexus.Collectors.Graph;
using Nexus.Collectors.Netskope;
using Nexus.Collectors.Sccm;
using Nexus.Collectors.Xdr;
using Nexus.Core;
using Nexus.Core.Configuration;
using Nexus.Data.Support;
using Nexus.Simulation;
using Nexus.Worker.Collection;
using Nexus.Worker.Platform;

namespace Nexus.Tests;

public sealed class FixedLoad(double? cpu) : IServerLoad
{
    public double? Cpu { get; set; } = cpu;
    public Task<double?> GetCpuPercentAsync(CancellationToken cancellationToken) => Task.FromResult(Cpu);
}

public sealed class SwitchableSources(SyntheticEstate estate) : ISourceFactory
{
    public bool FailDirectory { get; set; }
    public bool FailGraph { get; set; }
    public bool GraphEnabled { get; set; } = true;
    public bool XdrEnabled { get; set; } = true;
    public bool NetskopeEnabled { get; set; } = true;
    public bool FailNetskope { get; set; }

    public INetskopeReader? CreateNetskopeReader(NexusSettings settings)
    {
        if (!NetskopeEnabled)
        {
            return null;
        }

        var reader = (FakeNetskopeReader)estate.CreateNetskopeReader();
        reader.FailWithUnauthorized = FailNetskope;
        return reader;
    }
    public bool FailXdr { get; set; }

    public IXdrReader? CreateXdrReader(XdrSettings settings)
    {
        if (!XdrEnabled)
        {
            return null;
        }

        var reader = (FakeXdrReader)estate.CreateXdrReader();
        reader.FailWithUnreachable = FailXdr;
        return reader;
    }

    public IGraphReader? CreateGraphReader(NexusSettings settings)
    {
        if (!GraphEnabled)
        {
            return null;
        }

        var reader = (FakeGraphReader)estate.CreateGraphReader();
        reader.FailWithForbidden = FailGraph;
        return reader;
    }

    public bool FailSignIns { get; set; }

    public IGraphGovernanceReader? CreateGovernanceReader(NexusSettings settings)
    {
        if (!GraphEnabled)
        {
            return null;
        }

        var reader = (FakeGovernanceReader)estate.CreateGovernanceReader();
        reader.FailSignInsWithForbidden = FailSignIns;
        return reader;
    }

    public GraphConnection? CreateGraphConnection(NexusSettings settings) => null;

    public ISccmReader? CreateSccmReader(SccmSettings settings, SccmQueryGate gate) =>
        settings.Mode == SourceMode.Disabled ? null : estate.CreateSccmReader();

    public IDirectoryReader? CreateDirectoryReader(ActiveDirectorySettings settings) =>
        settings.Mode == SourceMode.Disabled ? null : new FakeDirectoryReader(estate.AdComputers) { FailWithUnreachable = FailDirectory };
}

public class JobRunnerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
    private readonly SyntheticEstate _estate = new(deviceCount: 120);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero));
    private readonly FixedLoad _load = new(10);
    private readonly SwitchableSources _sources;
    private readonly SettingsStore _store;
    private readonly JobRunner _runner;

    public JobRunnerTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var paths = new NexusPaths(_dataDir);
        _store = new SettingsStore(paths);
        var settings = new NexusSettings();
        settings.Sccm.Mode = SourceMode.Simulated;
        settings.ActiveDirectory.Mode = SourceMode.Simulated;
        _store.Save(settings);
        _sources = new SwitchableSources(_estate);
        _runner = new JobRunner(_db, new SettingsProvider(paths), _sources, new CollectionGate(_load, _clock), _clock, NullLogger<JobRunner>.Instance);
    }

    [Fact]
    public async Task FirstCollectionStoresEverySourceRecord()
    {
        Assert.Equal(JobRunner.StatusSucceeded, (await _runner.RunAsync(JobNames.Sccm, default)).Status);
        Assert.Equal(JobRunner.StatusSucceeded, (await _runner.RunAsync(JobNames.ActiveDirectory, default)).Status);

        await using var db = _db.Create();
        Assert.Equal(_estate.SccmSystems.Count, await db.SccmDevices.CountAsync());
        Assert.Equal(_estate.AdComputers.Count, await db.AdComputers.CountAsync());
        var job = await db.Jobs.SingleAsync(j => j.Name == JobNames.Sccm);
        Assert.Equal(_estate.SccmSystems.Count, job.LastRecordCount);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(30), job.NextRunAt);
    }

    [Fact]
    public async Task RunningTwiceGivesTheSameResult()
    {
        await _runner.RunAsync(JobNames.Sccm, default);
        await _runner.RunAsync(JobNames.Sccm, default);
        await using var db = _db.Create();
        Assert.Equal(_estate.SccmSystems.Count, await db.SccmDevices.CountAsync());
    }

    [Fact]
    public async Task FailureKeepsLastValidSnapshot()
    {
        await _runner.RunAsync(JobNames.ActiveDirectory, default);
        _sources.FailDirectory = true;
        var outcome = await _runner.RunAsync(JobNames.ActiveDirectory, default);

        Assert.Equal(JobRunner.StatusFailed, outcome.Status);
        await using var db = _db.Create();
        Assert.Equal(_estate.AdComputers.Count, await db.AdComputers.CountAsync());
        var job = await db.Jobs.SingleAsync(j => j.Name == JobNames.ActiveDirectory);
        Assert.NotNull(job.LastSuccessAt);
        Assert.Equal(JobRunner.StatusFailed, job.LastStatus);
    }

    [Fact]
    public async Task PauseWindowPostponesCollection()
    {
        var settings = _store.Load();
        settings.Collection.PauseWindows.Add(new PauseWindow { Name = "Backup do site", Start = new TimeOnly(14, 0), End = new TimeOnly(16, 0) });
        _store.Save(settings);

        var outcome = await _runner.RunAsync(JobNames.Sccm, default);
        Assert.Equal(JobRunner.StatusPostponed, outcome.Status);
        Assert.Contains("Backup do site", outcome.Message);
        await using var db = _db.Create();
        Assert.Equal(0, await db.SccmDevices.CountAsync());
    }

    [Fact]
    public async Task HighServerCpuPostponesCollection()
    {
        _load.Cpu = 95;
        var outcome = await _runner.RunAsync(JobNames.Sccm, default);
        Assert.Equal(JobRunner.StatusPostponed, outcome.Status);
    }

    [Fact]
    public async Task ManualPausePostponesCollection()
    {
        var settings = _store.Load();
        settings.Collection.Paused = true;
        _store.Save(settings);
        Assert.Equal(JobRunner.StatusPostponed, (await _runner.RunAsync(JobNames.ActiveDirectory, default)).Status);
    }

    [Fact]
    public async Task DisabledSourceIsNotConfiguredInsteadOfZero()
    {
        var settings = _store.Load();
        settings.Sccm.Mode = SourceMode.Disabled;
        _store.Save(settings);
        Assert.Equal(JobRunner.StatusNotConfigured, (await _runner.RunAsync(JobNames.Sccm, default)).Status);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }
}

public class SccmQueryGateTests
{
    [Fact]
    public async Task NeverExceedsConfiguredConcurrency()
    {
        var gate = new SccmQueryGate(new SccmSettings { MaxConcurrentQueries = 2 });
        var tasks = Enumerable.Range(0, 10).Select(_ => gate.RunAsync(async ct => { await Task.Delay(20, ct); return 0; }, default));
        await Task.WhenAll(tasks);
        Assert.Equal(2, gate.PeakConcurrency);
    }
}

public class ServiceIdentityTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData(".", true)]
    [InlineData(@"(local)\SCCM", true)]
    [InlineData("sql-remoto.azul.local", false)]
    public void DetectsLocalSqlServer(string server, bool local) => Assert.Equal(local, ServiceIdentity.IsLocal(server));
}

public class SyntheticEstateTests
{
    [Fact]
    public void IsDeterministicAndContainsEdgeCases()
    {
        var a = new SyntheticEstate(300);
        var b = new SyntheticEstate(300);
        Assert.Equal(a.SccmSystems.Select(s => s.Name), b.SccmSystems.Select(s => s.Name));
        Assert.Contains(a.SccmSystems, s => s.Client == false);
        Assert.Contains(a.SccmSystems, s => s.AadDeviceId is null);
        Assert.Contains(a.SccmSystems, s => s.Obsolete == true);
        Assert.Contains(a.Serials.Values, SyntheticEstate.InvalidSerials.Contains);
        Assert.Contains(a.AdComputers, c => !c.Enabled);
        Assert.Contains(a.AdComputers, c => a.SccmSystems.All(s => s.Name != c.Name)); // only in AD
    }

    [Fact]
    public void SimulatorScriptCreatesEveryViewUsedByTheCollector()
    {
        var script = SccmSimulatorScript.Create(new SyntheticEstate(10));
        Assert.All(SccmViews.All, view => Assert.Contains($"VIEW dbo.{view}", script));
    }
}
