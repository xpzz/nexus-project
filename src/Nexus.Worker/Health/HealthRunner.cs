using Nexus.Core;
using Nexus.Data.Support;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Nexus.Collectors.ActiveDirectory;
using Nexus.Collectors.Sccm;
using Nexus.Collectors.Netskope;
using Nexus.Collectors.Xdr;
using Nexus.Core.Configuration;
using Nexus.Collectors.Graph;
using Nexus.Core.Errors;
using Nexus.Core.Health;
using Nexus.Data;
using Nexus.Data.Entities;
using Nexus.Worker.Collection;
using Nexus.Worker.Platform;

namespace Nexus.Worker.Health;

/// <summary>
/// Runs every access check inside the Worker, with the Worker identity, and stores the results
/// for the Web, nexusctl and the install script (SPEC premise 5).
/// </summary>
public sealed class HealthRunner(SettingsProvider settingsProvider, INexusDbFactory dbFactory, ISourceFactory sources, TimeProvider clock, NexusPaths paths)
{
    public async Task<IReadOnlyList<HealthCheckResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<HealthCheckResult>();
        if (!settingsProvider.Exists)
        {
            results.Add(HealthCheckResult.Failed("Configuração", ErrorCatalog.ConfigurationMissing));
            return results;
        }

        var settings = settingsProvider.Current;
        results.Add(await CheckDatabaseAsync(settings, cancellationToken));
        foreach (var check in BuildChecks(settings))
        {
            var stopwatch = Stopwatch.StartNew();
            HealthCheckResult result;
            try
            {
                result = await check.CheckAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new HealthCheckResult(check.Name, HealthStatus.Error, ex.Message,
                    new NexusError("NEXUS-HC-999", $"Erro inesperado na verificação: {ex.Message}", "O item não pôde ser verificado.",
                        "Gere o pacote de diagnóstico e verifique os logs do Worker."));
            }

            results.Add(result with { Duration = result.Duration == default ? stopwatch.Elapsed : result.Duration });
        }

        await StoreAsync(results, cancellationToken);
        return results;
    }

    private IEnumerable<IHealthCheck> BuildChecks(NexusSettings settings)
    {
        var sccmAccount = ServiceIdentity.SqlLoginFor(settings.Sccm.SqlServer);
        if (settings.Sccm.Mode == SourceMode.Simulated)
        {
            yield return new StaticCheck("SCCM: leitura das views", HealthStatus.Ok, "Fonte simulada (modo demonstração ou simulador).");
        }
        else
        {
            yield return new SccmAccessCheck(settings.Sccm, sccmAccount);
        }

        var directory = sources.CreateDirectoryReader(settings.ActiveDirectory) ?? new FakeDirectoryReader([]);
        yield return new DirectoryHealthCheck(settings.ActiveDirectory, directory);
        yield return new GraphAccessCheck(settings, paths, sources);
        switch (settings.Netskope.Mode)
        {
            case SourceMode.Live:
                yield return new NetskopeAccessCheck(settings.Netskope, () => sources.CreateNetskopeReader(settings), clock);
                break;
            case SourceMode.Simulated:
                yield return new StaticCheck(NetskopeAccessCheck.CheckName, HealthStatus.Ok, "Fonte simulada (modo demonstração).");
                break;
        }

        switch (settings.Xdr.Mode)
        {
            case SourceMode.Live:
                yield return new XdrAccessCheck(settings.Xdr, sccmAccount, clock);
                break;
            case SourceMode.Simulated:
                yield return new StaticCheck(XdrAccessCheck.CheckName, HealthStatus.Ok, "Fonte simulada (modo demonstração).");
                break;
        }
    }

    private async Task<HealthCheckResult> CheckDatabaseAsync(NexusSettings settings, CancellationToken cancellationToken)
    {
        const string name = "Banco do Nexus";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var db = NexusDatabase.Create(settings.Database);
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                return new HealthCheckResult(name, HealthStatus.Error, ErrorCatalog.DatabaseMigrationsPending.WhatHappened,
                    ErrorCatalog.DatabaseMigrationsPending.WithDetail($"Pendentes: {string.Join(", ", pending)}."), stopwatch.Elapsed);
            }

            return new HealthCheckResult(name, HealthStatus.Ok,
                $"Conectado a {NexusDatabase.DatabaseName(settings.Database)} ({settings.Database.Provider}).", Duration: stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = ErrorCatalog.DatabaseUnreachable.WithDetail(ex.Message);
            return new HealthCheckResult(name, HealthStatus.Error, error.WhatHappened, error, stopwatch.Elapsed);
        }
    }

    private async Task StoreAsync(List<HealthCheckResult> results, CancellationToken cancellationToken)
    {
        if (results.Any(r => r.Name == "Banco do Nexus" && r.Status == HealthStatus.Error && r.Error?.Code == ErrorCatalog.DatabaseUnreachable.Code))
        {
            return; // nowhere to store; the caller reports the results directly
        }

        var runId = Guid.NewGuid();
        var executedAs = ServiceIdentity.Current();
        var now = clock.GetUtcNow();
        await using var db = dbFactory.Create();
        db.HealthResults.AddRange(results.Select(r => new HealthResultRecord
        {
            RunId = runId,
            Name = r.Name,
            Status = r.Status.ToString(),
            Message = r.Message,
            ErrorCode = r.Error?.Code,
            Impact = r.Error?.Impact,
            HowToFix = r.Error?.HowToFix,
            Script = r.Error?.Script,
            DurationMs = (int)r.Duration.TotalMilliseconds,
            CheckedAt = now,
            ExecutedAs = executedAs,
        }));
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed class StaticCheck(string name, HealthStatus status, string message) : IHealthCheck
    {
        public string Name => name;
        public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HealthCheckResult(name, status, message));
    }
}

public static class HealthQueries
{
    public static async Task<List<HealthResultRecord>> LatestAsync(NexusDbContext db, CancellationToken cancellationToken)
    {
        var lastRun = await db.HealthResults.OrderByDescending(h => h.Id).Select(h => (Guid?)h.RunId).FirstOrDefaultAsync(cancellationToken);
        return lastRun is null ? [] : await db.HealthResults.Where(h => h.RunId == lastRun).OrderBy(h => h.Id).ToListAsync(cancellationToken);
    }
}
