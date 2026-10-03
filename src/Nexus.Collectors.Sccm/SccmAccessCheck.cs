using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Nexus.Core.Configuration;
using Nexus.Core.Errors;
using Nexus.Core.Health;

namespace Nexus.Collectors.Sccm;

/// <summary>
/// Verifies, with the identity of the process that runs it (the Worker), that every view
/// can be read. 'SELECT TOP (0)' touches metadata only, no rows.
/// </summary>
public sealed class SccmAccessCheck(SccmSettings settings, string serviceAccount) : IHealthCheck
{
    private const int PermissionDenied = 229;
    private const int LoginFailed = 18456;
    private const int CannotOpenDatabase = 4060;

    public string Name => "SCCM: leitura das views";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (settings.Mode == SourceMode.Disabled || string.IsNullOrWhiteSpace(settings.SqlServer))
        {
            return HealthCheckResult.NotConfigured(Name, ErrorCatalog.SccmNotConfigured);
        }

        var stopwatch = Stopwatch.StartNew();
        var denied = new List<string>();
        var missing = new List<string>();
        try
        {
            await using var connection = new SqlConnection(SccmConnectionFactory.BuildConnectionString(settings));
            await connection.OpenAsync(cancellationToken);
            foreach (var view in SccmViews.All)
            {
                try
                {
                    await using var command = new SqlCommand($"SELECT TOP (0) * FROM [dbo].[{view}];", connection) { CommandTimeout = 30 };
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqlException ex) when (ex.Number == PermissionDenied)
                {
                    denied.Add(view);
                }
                catch (SqlException ex) when (ex.Number == 208)
                {
                    missing.Add(view);
                }
            }
        }
        catch (SqlException ex) when (ex.Number is LoginFailed or CannotOpenDatabase)
        {
            return Fail(ErrorCatalog.SccmLoginFailed.WithDetail(ex.Message), stopwatch);
        }
        catch (SqlException ex)
        {
            return Fail(ErrorCatalog.SccmSqlUnreachable.WithDetail(ex.Message), stopwatch);
        }

        if (denied.Count > 0)
        {
            var error = ErrorCatalog.SccmViewAccessDenied
                .WithDetail($"Sem SELECT em: {string.Join(", ", denied)}.")
                .WithScript(SccmGrantScript.Grant(settings.Database, serviceAccount));
            return Fail(error, stopwatch);
        }

        var message = $"{SccmViews.All.Count - missing.Count} views legíveis no banco {settings.Database}.";
        if (missing.Count > 0)
        {
            return new HealthCheckResult(Name, HealthStatus.Warning,
                $"{message} Views inexistentes neste site (classe de inventário desabilitada ou versão): {string.Join(", ", missing)}.",
                Duration: stopwatch.Elapsed);
        }

        return new HealthCheckResult(Name, HealthStatus.Ok, message, Duration: stopwatch.Elapsed);
    }

    private HealthCheckResult Fail(NexusError error, Stopwatch stopwatch) =>
        new(Name, HealthStatus.Error, error.WhatHappened, error, stopwatch.Elapsed);
}
