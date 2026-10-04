using Nexus.Data.Support;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Nexus.Collectors.Graph;
using Nexus.Collectors.Sccm;
using Nexus.Collectors.Netskope;
using Nexus.Collectors.Xdr;
using Nexus.Core.Configuration;
using Nexus.Data;
using Nexus.Data.Entities;
using Nexus.Reconciliation;

namespace Nexus.Worker.Collection;

public static class JobNames
{
    public const string Sccm = "sccm.devices";
    public const string ActiveDirectory = "ad.computers";
    public const string Intune = "intune.devices";
    public const string Entra = "entra.devices";
    public const string Xdr = "xdr.endpoints";
    public const string Netskope = "netskope.clients";
    public const string Mam = "intune.mam";
    public const string Users = "entra.users";
    public const string Policies = "intune.policies";
    public const string Reconcile = "inventory.reconcile";
    // Order matters: users are resolved from the devices and MAM registrations collected just before.
    public static readonly string[] All = [Sccm, ActiveDirectory, Intune, Entra, Xdr, Netskope, Mam, Users, Policies, Reconcile];
    public static readonly string[] Collections = [Sccm, ActiveDirectory, Intune, Entra, Xdr, Netskope, Mam, Users, Policies];
}

public sealed record JobOutcome(string Status, int? Records, string? Message);

/// <summary>
/// Runs collections without overlap. A failure keeps the last valid snapshot untouched
/// (the replacement happens in one transaction) so coverage never drops artificially (SPEC §10).
/// </summary>
public sealed class JobRunner(
    INexusDbFactory dbFactory,
    SettingsProvider settingsProvider,
    ISourceFactory sources,
    CollectionGate gate,
    TimeProvider clock,
    ILogger<JobRunner> logger)
{
    public const string StatusSucceeded = "Concluída";
    public const string StatusFailed = "Falhou";
    public const string StatusPostponed = "Adiada";
    public const string StatusNotConfigured = "Não configurada";

    private static readonly TimeSpan PostponeDelay = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, SemaphoreSlim> _locks = JobNames.All.ToDictionary(n => n, _ => new SemaphoreSlim(1, 1));
    private bool _slowQueryObserved;

    public TimeSpan IntervalOf(string job, NexusSettings settings) => job switch
    {
        JobNames.Sccm => TimeSpan.FromMinutes(settings.Collection.SccmIntervalMinutes),
        JobNames.ActiveDirectory => TimeSpan.FromMinutes(settings.Collection.ActiveDirectoryIntervalMinutes),
        JobNames.Intune or JobNames.Entra => TimeSpan.FromMinutes(settings.Collection.GraphIntervalMinutes),
        JobNames.Xdr => TimeSpan.FromMinutes(settings.Collection.XdrIntervalMinutes),
        JobNames.Netskope => TimeSpan.FromMinutes(settings.Collection.NetskopeIntervalMinutes),
        JobNames.Mam => TimeSpan.FromMinutes(settings.Collection.MamIntervalMinutes),
        JobNames.Users => TimeSpan.FromMinutes(settings.Collection.UsersIntervalMinutes),
        JobNames.Policies => TimeSpan.FromMinutes(settings.Collection.PoliciesIntervalMinutes),
        JobNames.Reconcile => TimeSpan.FromMinutes(settings.Collection.ReconcileIntervalMinutes),
        _ => throw new ArgumentOutOfRangeException(nameof(job), job, "Coleta desconhecida."),
    };

    public async Task<JobOutcome> RunAsync(string job, CancellationToken cancellationToken)
    {
        var jobLock = _locks[job];
        if (!await jobLock.WaitAsync(0, cancellationToken))
        {
            return new JobOutcome("Em execução", null, "A coleta já está em execução.");
        }

        try
        {
            return await RunLockedAsync(job, cancellationToken);
        }
        finally
        {
            jobLock.Release();
        }
    }

    private async Task<JobOutcome> RunLockedAsync(string job, CancellationToken cancellationToken)
    {
        var settings = settingsProvider.Current;
        var now = clock.GetUtcNow();

        var blocked = await gate.CheckAsync(settings.Collection, job == JobNames.Sccm && _slowQueryObserved, cancellationToken);
        if (blocked is not null)
        {
            _slowQueryObserved = false; // one postponement per slow query
            var outcome = new JobOutcome(StatusPostponed, null, blocked.ToString());
            await SaveStateAsync(job, now, outcome, TimeSpan.Zero, now + PostponeDelay, cancellationToken);
            logger.LogInformation("Coleta {Job} adiada: {Reason}", job, blocked.WhatHappened);
            return outcome;
        }

        var stopwatch = Stopwatch.StartNew();
        JobOutcome result;
        try
        {
            var records = job switch
            {
                JobNames.Sccm => await CollectSccmAsync(settings, now, cancellationToken),
                JobNames.ActiveDirectory => await CollectDirectoryAsync(settings, now, cancellationToken),
                JobNames.Intune => await CollectIntuneAsync(settings, now, cancellationToken),
                JobNames.Entra => await CollectEntraAsync(settings, now, cancellationToken),
                JobNames.Xdr => await CollectXdrAsync(settings, now, cancellationToken),
                JobNames.Netskope => await CollectNetskopeAsync(settings, now, cancellationToken),
                JobNames.Mam => await CollectMamAsync(settings, now, cancellationToken),
                JobNames.Users => await CollectUsersAsync(settings, now, cancellationToken),
                JobNames.Policies => await CollectPoliciesAsync(settings, now, cancellationToken),
                JobNames.Reconcile => await ReconcileAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(job)),
            };
            result = records is null
                ? new JobOutcome(StatusNotConfigured, null, "Fonte não configurada.")
                : new JobOutcome(StatusSucceeded, records, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha na coleta {Job}", job);
            result = new JobOutcome(StatusFailed, null, ex.Message);
        }

        await SaveStateAsync(job, now, result, stopwatch.Elapsed, now + IntervalOf(job, settings), cancellationToken);
        if (result.Status == StatusSucceeded && job != JobNames.Reconcile)
        {
            await RunAsync(JobNames.Reconcile, cancellationToken); // new data: refresh the reconciled view
        }

        return result;
    }

    private async Task<int?> ReconcileAsync(CancellationToken cancellationToken)
    {
        await using (var db = dbFactory.Create())
        {
            var anySuccess = await db.Jobs.AnyAsync(j => JobNames.Collections.Contains(j.Name) && j.LastSuccessAt != null, cancellationToken);
            if (!anySuccess)
            {
                return null; // nothing collected yet: there is nothing to reconcile
            }
        }

        var policy = EvidencePolicy.From(settingsProvider.Current.Evidence);
        var result = await new InventoryReconciler(dbFactory, clock, TimeSpan.FromDays(policy.Default.ProbableDays), policy).RunAsync(cancellationToken);
        logger.LogInformation("Reconciliação: {Assets} ativos, {Review} itens para revisão.", result.Assets.Count, result.Review.Count);
        return result.Assets.Count;
    }

    private async Task<int?> CollectIntuneAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateGraphReader(settings);
        if (reader is null)
        {
            return null;
        }

        var rows = new Dictionary<string, IntuneDeviceRecord>();
        await foreach (var d in reader.ReadManagedDevicesAsync(cancellationToken))
        {
            rows[d.Id] = new IntuneDeviceRecord
            {
                Id = d.Id, DeviceName = d.DeviceName, AzureAdDeviceId = d.AzureAdDeviceId, SerialNumber = d.SerialNumber,
                Manufacturer = d.Manufacturer, Model = d.Model, OperatingSystem = d.OperatingSystem, OsVersion = d.OsVersion,
                ManagementAgent = d.ManagementAgent, EnrollmentType = d.EnrollmentType, OwnerType = d.OwnerType,
                LastSyncAt = d.LastSyncAt, EnrolledAt = d.EnrolledAt, ComplianceState = d.ComplianceState,
                UserPrincipalName = d.UserPrincipalName, UserId = d.UserId, IsEncrypted = d.IsEncrypted, JailBroken = d.JailBroken, IsSupervised = d.IsSupervised,
                TotalStorageBytes = d.TotalStorageBytes, FreeStorageBytes = d.FreeStorageBytes, PhysicalMemoryBytes = d.PhysicalMemoryBytes,
                DeviceRegistrationState = d.DeviceRegistrationState, AutopilotEnrolled = d.AutopilotEnrolled, ComplianceGraceExpiresAt = d.ComplianceGraceExpiresAt, CollectedAt = now,
            };
        }

        if (reader is HttpGraphReader { UsedExtendedDeviceFields: false })
        {
            logger.LogWarning("Este tenant recusou os campos estendidos dos dispositivos (criptografia, armazenamento, jailbreak); coletado só o básico.");
        }

        await ReplaceAsync<IntuneDeviceRecord>(rows.Values.ToList(), cancellationToken);
        return rows.Count;
    }

    private async Task<int?> CollectEntraAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateGraphReader(settings);
        if (reader is null)
        {
            return null;
        }

        var rows = new Dictionary<string, EntraDeviceRecord>();
        await foreach (var d in reader.ReadEntraDevicesAsync(cancellationToken))
        {
            rows[d.Id] = new EntraDeviceRecord
            {
                Id = d.Id, DeviceId = d.DeviceId, DisplayName = d.DisplayName, TrustType = d.TrustType, LastSignInAt = d.LastSignInAt,
                AccountEnabled = d.AccountEnabled, OperatingSystem = d.OperatingSystem, OperatingSystemVersion = d.OperatingSystemVersion,
                Ownership = d.Ownership, RegisteredAt = d.RegisteredAt, CollectedAt = now,
            };
        }

        await ReplaceAsync<EntraDeviceRecord>(rows.Values.ToList(), cancellationToken);
        return rows.Count;
    }

    private async Task<int?> CollectSccmAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queryGate = new SccmQueryGate(settings.Sccm);
        var reader = sources.CreateSccmReader(settings.Sccm, queryGate);
        if (reader is null)
        {
            return null;
        }

        var rows = new Dictionary<int, SccmDeviceRecord>(); // one row per ResourceID even if a joined view repeats it
        await foreach (var s in reader.ReadSystemsAsync(cancellationToken))
        {
            rows[s.ResourceId] = new SccmDeviceRecord
            {
                ResourceId = s.ResourceId, Name = s.Name, Domain = s.Domain, Client = s.Client, Active = s.Active,
                Obsolete = s.Obsolete, AadDeviceId = s.AadDeviceId, SmbiosGuid = s.SmbiosGuid,
                OperatingSystem = s.OperatingSystem, Serial = s.Serial, Manufacturer = s.Manufacturer, Model = s.Model,
                LastActiveAt = s.LastActiveAt, ClientActiveStatus = s.ClientActiveStatus, CollectedAt = now,
                ClientVersion = s.ClientVersion, LastPolicyRequestAt = s.LastPolicyRequestAt, LastHwScanAt = s.LastHwScanAt, LastSwScanAt = s.LastSwScanAt,
                LastDdrAt = s.LastDdrAt, LastLogonUser = s.LastLogonUser, AdSite = s.AdSite, OsVersion = s.OsVersion, LastBootAt = s.LastBootAt,
                CpuName = s.CpuName, CpuCores = s.CpuCores, MemoryMb = s.MemoryMb, DiskTotalMb = s.DiskTotalMb, DiskFreeMb = s.DiskFreeMb, BiosVersion = s.BiosVersion,
            };
        }

        if (reader is SqlSccmReader { FallbackReason: { } reason })
        {
            logger.LogWarning("{Reason}", reason);
        }

        _slowQueryObserved = queryGate.SlowQueryObserved;
        await ReplaceAsync<SccmDeviceRecord>(rows.Values.ToList(), cancellationToken);
        return rows.Count;
    }

    private async Task<int?> CollectDirectoryAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateDirectoryReader(settings.ActiveDirectory);
        if (reader is null)
        {
            return null;
        }

        var rows = new Dictionary<Guid, AdComputerRecord>();
        await foreach (var c in reader.ReadComputersAsync(cancellationToken))
        {
            rows[c.ObjectGuid] = new AdComputerRecord
            {
                ObjectGuid = c.ObjectGuid, Name = c.Name, DnsHostName = c.DnsHostName, OperatingSystem = c.OperatingSystem,
                OperatingSystemVersion = c.OperatingSystemVersion, LastLogonTimestamp = c.LastLogonTimestamp,
                PasswordLastSet = c.PasswordLastSet, WhenCreated = c.WhenCreated, Enabled = c.Enabled,
                DistinguishedName = c.DistinguishedName, CollectedAt = now,
            };
        }

        await ReplaceAsync<AdComputerRecord>(rows.Values.ToList(), cancellationToken);
        return rows.Count;
    }

    private async Task<int?> CollectXdrAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateXdrReader(settings.Xdr);
        if (reader is null)
        {
            return null;
        }

        var rows = new Dictionary<string, XdrEndpointRecord>();
        await foreach (var e in reader.ReadEndpointsAsync(cancellationToken))
        {
            rows[e.AgentId] = new XdrEndpointRecord
            {
                AgentId = e.AgentId, HostName = e.HostName, AgentStatus = e.AgentStatus, OperationalStatus = e.OperationalStatus,
                AgentType = e.AgentType, Ip = e.Ip, LastSeenAt = e.LastSeen, Users = e.Users, CollectedAt = now,
            };
        }

        await ReplaceAsync<XdrEndpointRecord>(rows.Values.ToList(), cancellationToken);
        return rows.Count;
    }

    private async Task<int?> CollectNetskopeAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateNetskopeReader(settings);
        if (reader is null)
        {
            return null;
        }

        var rows = new Dictionary<string, NetskopeClientRecord>();
        await foreach (var c in reader.ReadClientsAsync(cancellationToken))
        {
            rows[c.Id] = new NetskopeClientRecord
            {
                Id = c.Id, DeviceId = c.DeviceId, HostName = c.HostName, OperatingSystem = c.OperatingSystem, OsVersion = c.OsVersion, Serial = c.Serial,
                Manufacturer = c.Manufacturer, Model = c.Model, ClientVersion = c.ClientVersion, Status = c.Status, LastEventAt = c.LastEventAt,
                InstalledAt = c.InstalledAt, ManagementId = c.ManagementId, Users = c.Users, CollectedAt = now,
            };
        }

        await ReplaceAsync<NetskopeClientRecord>(rows.Values.ToList(), cancellationToken);
        return rows.Count;
    }

    private async Task<int?> CollectMamAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateGraphReader(settings);
        if (reader is null)
        {
            return null;
        }

        var rows = new Dictionary<string, MamRegistrationRecord>();
        await foreach (var r in reader.ReadMamRegistrationsAsync(cancellationToken))
        {
            rows[r.Id] = new MamRegistrationRecord
            {
                Id = r.Id, UserId = r.UserId, DeviceName = r.DeviceName, DeviceTag = r.DeviceTag, DeviceType = r.DeviceType, AppIdentifier = r.AppIdentifier,
                AppVersion = r.AppVersion, PlatformVersion = r.PlatformVersion, LastSyncAt = r.LastSyncAt, CreatedAt = r.CreatedAt, FlaggedReasons = r.FlaggedReasons,
                AppliedPolicies = r.AppliedPolicies, IntendedPolicies = r.IntendedPolicies, CollectedAt = now,
            };
        }

        await ReplaceAsync<MamRegistrationRecord>(rows.Values.ToList(), cancellationToken);
        return rows.Count;
    }

    private async Task<int?> CollectUsersAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateGraphReader(settings);
        if (reader is null)
        {
            return null;
        }

        List<string> ids;
        await using (var db = dbFactory.Create())
        {
            var fromDevices = await db.IntuneDevices.AsNoTracking().Where(d => d.UserId != null).Select(d => d.UserId!).Distinct().ToListAsync(cancellationToken);
            var fromMam = await db.MamRegistrations.AsNoTracking().Where(r => r.UserId != null).Select(r => r.UserId!).Distinct().ToListAsync(cancellationToken);
            ids = fromDevices.Concat(fromMam).Distinct().ToList();
        }

        var rows = new Dictionary<string, EntraUserRecord>();
        foreach (var chunk in ids.Chunk(200))
        {
            await foreach (var u in reader.ReadUsersAsync(chunk, cancellationToken))
            {
                rows[u.Id] = new EntraUserRecord
                {
                    Id = u.Id, UserPrincipalName = u.UserPrincipalName, DisplayName = u.DisplayName, Department = u.Department, AccountEnabled = u.AccountEnabled, CollectedAt = now,
                };
            }
        }

        await ReplaceAsync<EntraUserRecord>(rows.Values.ToList(), cancellationToken);
        return rows.Count;
    }

    private async Task<int?> CollectPoliciesAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateGraphReader(settings);
        if (reader is null)
        {
            return null;
        }

        var policies = new Dictionary<string, IntunePolicyRecord>();
        await foreach (var p in reader.ReadPoliciesAsync(cancellationToken))
        {
            var key = $"{p.Kind}:{p.Id}";
            policies[key] = new IntunePolicyRecord
            {
                Key = key, Kind = p.Kind, PolicyId = p.Id, Name = p.Name, Description = p.Description, Platform = p.Platform, Version = p.Version,
                LastModifiedAt = p.LastModifiedAt, Assignments = p.Assignments, AssignedToAll = p.AssignedToAll, AssignmentCount = p.AssignmentCount, CollectedAt = now,
            };
        }

        List<string> deviceIds;
        await using (var db = dbFactory.Create())
        {
            deviceIds = await db.IntuneDevices.AsNoTracking().Where(d => d.ManagementAgent != null && d.ManagementAgent.ToLower().Contains("mdm")).Select(d => d.Id).ToListAsync(cancellationToken);
        }

        var states = new List<IntuneDevicePolicyState>();
        foreach (var chunk in deviceIds.Chunk(500))
        {
            await foreach (var s in reader.ReadDevicePolicyStatesAsync(chunk, cancellationToken))
            {
                states.Add(new IntuneDevicePolicyState
                {
                    IntuneDeviceId = s.ManagedDeviceId, Kind = s.Kind, PolicyId = s.PolicyId, PolicyName = s.PolicyName, State = s.State,
                    Platform = s.Platform, SettingCount = s.SettingCount, Version = s.Version, CollectedAt = now,
                });
            }
        }

        // Both tables change together or not at all: a half-read run never replaces a good snapshot.
        await ReplaceAsync<IntunePolicyRecord>(policies.Values.ToList(), cancellationToken);
        await ReplaceAsync<IntuneDevicePolicyState>(states, cancellationToken);
        return policies.Count;
    }

    private async Task ReplaceAsync<T>(List<T> rows, CancellationToken cancellationToken) where T : class
    {
        await using var db = dbFactory.Create();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Set<T>().ExecuteDeleteAsync(cancellationToken);
            foreach (var chunk in rows.Chunk(1000))
            {
                db.Set<T>().AddRange(chunk);
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task SaveStateAsync(string job, DateTimeOffset startedAt, JobOutcome outcome, TimeSpan duration, DateTimeOffset nextRun, CancellationToken cancellationToken)
    {
        await using var db = dbFactory.Create();
        var state = await db.Jobs.FirstOrDefaultAsync(j => j.Name == job, cancellationToken);
        if (state is null)
        {
            state = new JobState { Name = job };
            db.Jobs.Add(state);
        }

        state.LastStartedAt = startedAt;
        state.LastCompletedAt = clock.GetUtcNow();
        state.LastStatus = outcome.Status;
        state.LastDurationMs = (int)duration.TotalMilliseconds;
        state.LastError = outcome.Message;
        state.NextRunAt = nextRun;
        if (outcome.Status == StatusSucceeded)
        {
            state.LastSuccessAt = state.LastCompletedAt;
            state.LastRecordCount = outcome.Records;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
