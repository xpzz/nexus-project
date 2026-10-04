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
    public const string AppPolicies = "intune.apppolicies";
    public const string ConditionalAccess = "entra.ca";
    public const string SignIns = "entra.signins";
    public const string Reconcile = "inventory.reconcile";
    // Order matters: users are resolved from the devices and MAM registrations collected just before.
    public static readonly string[] All = [Sccm, ActiveDirectory, Intune, Entra, Xdr, Netskope, Mam, Users, Policies, AppPolicies, ConditionalAccess, SignIns, Reconcile];
    public static readonly string[] Collections = [Sccm, ActiveDirectory, Intune, Entra, Xdr, Netskope, Mam, Users, Policies, AppPolicies, ConditionalAccess, SignIns];
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

    /// <summary>Run ID of the job being executed; flows to the history tables and the logs.</summary>
    private static readonly AsyncLocal<Guid> CurrentRunId = new();

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
        JobNames.AppPolicies => TimeSpan.FromMinutes(settings.Governance.AppPoliciesIntervalMinutes),
        JobNames.ConditionalAccess => TimeSpan.FromMinutes(settings.Governance.ConditionalAccessIntervalMinutes),
        JobNames.SignIns => TimeSpan.FromMinutes(settings.Governance.SignInsIntervalMinutes),
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
        var runId = Guid.NewGuid();
        CurrentRunId.Value = runId;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = runId, ["Job"] = job });

        var blocked = await gate.CheckAsync(settings.Collection, job == JobNames.Sccm && _slowQueryObserved, cancellationToken);
        if (blocked is not null)
        {
            _slowQueryObserved = false; // one postponement per slow query
            var outcome = new JobOutcome(StatusPostponed, null, blocked.ToString());
            await SaveStateAsync(job, now, outcome, TimeSpan.Zero, now + PostponeDelay, cancellationToken, runId);
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
                JobNames.AppPolicies => await CollectAppPoliciesAsync(settings, now, cancellationToken),
                JobNames.ConditionalAccess => await CollectConditionalAccessAsync(settings, now, cancellationToken),
                JobNames.SignIns => await CollectSignInsAsync(settings, now, cancellationToken),
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

        await SaveStateAsync(job, now, result, stopwatch.Elapsed, now + IntervalOf(job, settings), cancellationToken, runId);
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
        await PurgeHistoryAsync(cancellationToken);
        return result.Assets.Count;
    }

    private async Task PurgeHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var retention = Math.Max(30, settingsProvider.Current.Collection.HistoryRetentionDays);
            var limit = clock.GetUtcNow() - TimeSpan.FromDays(retention);
            await using var db = dbFactory.Create();
            var versions = await RawRecordArchive.PurgeAsync(db, clock.GetUtcNow(), retention, cancellationToken);
            var runs = await db.JobRuns.Where(r => r.StartedAt < limit).ExecuteDeleteAsync(cancellationToken);
            var timeline = await db.EvidenceTimeline.Where(e => e.CollectedAt < limit).ExecuteDeleteAsync(cancellationToken);
            var changes = await db.AssetChanges.Where(c => c.At < limit).ExecuteDeleteAsync(cancellationToken);
            if (versions + runs + timeline + changes > 0)
            {
                logger.LogInformation("Histórico expirado removido (retenção de {Days} dias): {Versions} versões, {Runs} execuções, {Timeline} datas de evidência, {Changes} mudanças.", retention, versions, runs, timeline, changes);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A limpeza do histórico falhou e será tentada na próxima reconciliação.");
        }
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
                DeviceRegistrationState = d.DeviceRegistrationState, AutopilotEnrolled = d.AutopilotEnrolled, ComplianceGraceExpiresAt = d.ComplianceGraceExpiresAt, EthernetMac = NetworkIds.NormalizeMac(d.EthernetMac), WifiMac = NetworkIds.NormalizeMac(d.WifiMac), CollectedAt = now,
            };
        }

        if (reader is HttpGraphReader { UsedExtendedDeviceFields: false })
        {
            logger.LogWarning("Este tenant recusou os campos estendidos dos dispositivos (criptografia, armazenamento, jailbreak); coletado só o básico.");
        }

        await ReplaceAsync<IntuneDeviceRecord>(rows.Values.ToList(), cancellationToken, "intune", r => r.Id);
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

        await ReplaceAsync<EntraDeviceRecord>(rows.Values.ToList(), cancellationToken, "entra", r => r.Id);
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

        try
        {
            var extras = await reader.ReadExtrasAsync(cancellationToken);
            foreach (var (id, e) in extras.Where(x => rows.ContainsKey(x.Key)))
            {
                rows[id].MacAddresses = e.Macs.Count == 0 ? null : string.Join(",", e.Macs);
                rows[id].IpAddresses = e.Ips.Count == 0 ? null : string.Join(",", e.Ips.Take(8));
                rows[id].Chassis = e.Chassis;
            }

            if (reader is SqlSccmReader { ExtrasWarning: { } warning })
            {
                logger.LogWarning("{Warning}", warning);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // MAC, IP and chassis are enrichment: the devices themselves were read, so the collection must not fail because of them.
            logger.LogWarning(ex, "Não foi possível ler MAC, IP e chassi do SCCM; a coleta dos dispositivos continua.");
        }

        _slowQueryObserved = queryGate.SlowQueryObserved;
        await ReplaceAsync<SccmDeviceRecord>(rows.Values.ToList(), cancellationToken, "sccm", r => r.ResourceId.ToString());
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

        await ReplaceAsync<AdComputerRecord>(rows.Values.ToList(), cancellationToken, "ad", r => r.ObjectGuid.ToString());
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

        await ReplaceAsync<XdrEndpointRecord>(rows.Values.ToList(), cancellationToken, "xdr", r => r.AgentId);
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

        await ReplaceAsync<NetskopeClientRecord>(rows.Values.ToList(), cancellationToken, "netskope", r => r.Id);
        return rows.Count;
    }

    private async Task<int?> CollectAppPoliciesAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateGovernanceReader(settings);
        if (reader is null)
        {
            return null;
        }

        var policies = new Dictionary<string, AppProtectionPolicyRecord>();
        await foreach (var p in reader.ReadAppProtectionPoliciesAsync(cancellationToken))
        {
            policies[p.Id] = new AppProtectionPolicyRecord
            {
                Id = p.Id, Platform = p.Platform, Name = p.Name, LastModifiedAt = p.LastModifiedAt, Version = p.Version, IsAssigned = p.IsAssigned, AssignedToAll = p.AssignedToAll,
                Assignments = p.Assignments, AppsJson = System.Text.Json.JsonSerializer.Serialize(p.Apps), SettingsJson = System.Text.Json.JsonSerializer.Serialize(p.Settings), CollectedAt = now,
            };
        }

        var configs = new Dictionary<string, AppConfigRecord>();
        await foreach (var c in reader.ReadAppConfigurationsAsync(cancellationToken))
        {
            configs[c.Id] = new AppConfigRecord
            {
                Id = c.Id, Kind = c.Kind, Platform = c.Platform, Name = c.Name, LastModifiedAt = c.LastModifiedAt, Assignments = c.Assignments,
                AppsJson = System.Text.Json.JsonSerializer.Serialize(c.Apps), SettingsJson = System.Text.Json.JsonSerializer.Serialize(c.Settings), CollectedAt = now,
            };
        }

        await ReplaceAsync<AppProtectionPolicyRecord>(policies.Values.ToList(), cancellationToken, "app-policy", r => r.Id);
        await ReplaceAsync<AppConfigRecord>(configs.Values.ToList(), cancellationToken, "app-config", r => r.Id);
        return policies.Count + configs.Count;
    }

    private async Task<int?> CollectConditionalAccessAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateGovernanceReader(settings);
        if (reader is null)
        {
            return null;
        }

        var rows = new Dictionary<string, ConditionalAccessRecord>();
        await foreach (var c in reader.ReadConditionalAccessAsync(cancellationToken))
        {
            rows[c.Id] = new ConditionalAccessRecord
            {
                Id = c.Id, Name = c.Name, State = c.State, ModifiedAt = c.ModifiedAt, Users = c.Users, Applications = c.Applications, Platforms = c.Platforms, GrantControls = string.Join(",", c.GrantControls),
                RequiresCompliantDevice = c.RequiresCompliantDevice, RequiresApprovedApp = c.RequiresApprovedApp, RequiresAppProtection = c.RequiresAppProtection, RequiresMfa = c.RequiresMfa,
                TargetsMicrosoft365 = c.TargetsMicrosoft365, CollectedAt = now,
            };
        }

        await ReplaceAsync<ConditionalAccessRecord>(rows.Values.ToList(), cancellationToken, "ca-policy", r => r.Id);
        return rows.Count;
    }

    private async Task<int?> CollectSignInsAsync(NexusSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reader = sources.CreateGovernanceReader(settings);
        if (reader is null)
        {
            return null;
        }

        var since = now - TimeSpan.FromDays(Math.Clamp(settings.Governance.SignInWindowDays, 1, 30));
        var rows = new Dictionary<string, AccessEvidenceRecord>();
        await foreach (var a in reader.ReadSignInAccessAsync(since, Math.Clamp(settings.Governance.MaxSignInPages, 1, 400), cancellationToken))
        {
            rows[a.Key] = new AccessEvidenceRecord
            {
                Key = a.Key, UserId = a.UserId, UserPrincipalName = a.UserPrincipalName, EntraDeviceId = a.EntraDeviceId, DeviceName = a.DeviceName, OperatingSystem = a.OperatingSystem,
                Browser = a.Browser, IsManaged = a.IsManaged, IsCompliant = a.IsCompliant, TrustType = a.TrustType, LastAccessAt = a.LastAccessAt, Workloads = a.Workloads, Count = a.Count,
                ClientApp = a.ClientApp, CollectedAt = now,
            };
        }

        await ReplaceAsync<AccessEvidenceRecord>(rows.Values.ToList(), cancellationToken); // volatile by nature: the asset timeline keeps the dates
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
                AppliedPolicies = r.AppliedPolicies, IntendedPolicies = r.IntendedPolicies, LastOperation = r.LastOperation, CollectedAt = now,
            };
        }

        await ReplaceAsync<MamRegistrationRecord>(rows.Values.ToList(), cancellationToken, "mam", r => r.Id);
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
            ids = GraphIds.Clean(fromDevices.Concat(fromMam)); // blank, zero-GUID and repeated ids would break the $batch request
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

        await ReplaceAsync<EntraUserRecord>(rows.Values.ToList(), cancellationToken, "entra-user", r => r.Id);
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

    /// <summary>Replaces the raw table in one transaction (a failure keeps the last snapshot) and then archives the versions that changed.</summary>
    private async Task ReplaceAsync<T>(List<T> rows, CancellationToken cancellationToken, string? archiveSource = null, Func<T, string>? archiveKey = null) where T : class
    {
        await ReplaceTableAsync(rows, cancellationToken);
        if (archiveSource is null || archiveKey is null)
        {
            return;
        }

        try
        {
            await using var db = dbFactory.Create();
            var result = await RawRecordArchive.ArchiveAsync(db, archiveSource, rows, archiveKey, clock.GetUtcNow(), CurrentRunId.Value, cancellationToken);
            logger.LogInformation("Versões de {Source}: {Created} novas, {Changed} alteradas, {Unchanged} iguais, {Removed} removidas (run {RunId}).", archiveSource, result.Created, result.Changed, result.Unchanged, result.Removed, CurrentRunId.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The collection itself succeeded; losing one history write must not turn it into a failure.
            logger.LogWarning(ex, "Não foi possível arquivar as versões de {Source}. A coleta foi concluída; o histórico desta rodada ficou incompleto.", archiveSource);
        }
    }

    private async Task ReplaceTableAsync<T>(List<T> rows, CancellationToken cancellationToken) where T : class
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

    private async Task SaveStateAsync(string job, DateTimeOffset startedAt, JobOutcome outcome, TimeSpan duration, DateTimeOffset nextRun, CancellationToken cancellationToken, Guid runId)
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

        db.JobRuns.Add(new JobRun
        {
            RunId = runId, Job = job, StartedAt = startedAt, CompletedAt = state.LastCompletedAt!.Value, Status = outcome.Status, Records = outcome.Records,
            DurationMs = (int)duration.TotalMilliseconds, Message = outcome.Message is { Length: > 2000 } m ? m[..2000] : outcome.Message,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
