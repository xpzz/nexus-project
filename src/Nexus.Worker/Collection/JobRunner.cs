using Nexus.Data.Support;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Nexus.Collectors.Sccm;
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
    public const string Reconcile = "inventory.reconcile";
    public static readonly string[] All = [Sccm, ActiveDirectory, Intune, Entra, Reconcile];
    public static readonly string[] Collections = [Sccm, ActiveDirectory, Intune, Entra];
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

        var result = await new InventoryReconciler(dbFactory, clock).RunAsync(cancellationToken);
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
                UserPrincipalName = d.UserPrincipalName, CollectedAt = now,
            };
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

        var rows = new List<SccmDeviceRecord>();
        await foreach (var s in reader.ReadSystemsAsync(cancellationToken))
        {
            rows.Add(new SccmDeviceRecord
            {
                ResourceId = s.ResourceId, Name = s.Name, Domain = s.Domain, Client = s.Client, Active = s.Active,
                Obsolete = s.Obsolete, AadDeviceId = s.AadDeviceId, SmbiosGuid = s.SmbiosGuid,
                OperatingSystem = s.OperatingSystem, Serial = s.Serial, Manufacturer = s.Manufacturer, Model = s.Model,
                LastActiveAt = s.LastActiveAt, ClientActiveStatus = s.ClientActiveStatus, CollectedAt = now,
            });
        }

        if (reader is SqlSccmReader { UsedExtendedQuery: false, FallbackReason: { } reason })
        {
            logger.LogWarning("{Reason}", reason);
        }

        _slowQueryObserved = queryGate.SlowQueryObserved;
        await ReplaceAsync<SccmDeviceRecord>(rows, cancellationToken);
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
