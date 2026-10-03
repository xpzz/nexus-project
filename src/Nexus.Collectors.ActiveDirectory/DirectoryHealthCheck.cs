using System.Diagnostics;
using Nexus.Core.Configuration;
using Nexus.Core.Errors;
using Nexus.Core.Health;

namespace Nexus.Collectors.ActiveDirectory;

public sealed class DirectoryHealthCheck(ActiveDirectorySettings settings, IDirectoryReader reader) : IHealthCheck
{
    public string Name => "Active Directory: leitura de computadores";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (settings.Mode == SourceMode.Disabled)
        {
            return HealthCheckResult.NotConfigured(Name, ErrorCatalog.ActiveDirectoryNotConfigured);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await foreach (var _ in reader.ReadComputersAsync(cancellationToken))
            {
                return new HealthCheckResult(Name, HealthStatus.Ok,
                    $"Leitura confirmada no domínio {settings.Domain}{(settings.Mode == SourceMode.Simulated ? " (simulado)" : "")}.",
                    Duration: stopwatch.Elapsed);
            }

            return new HealthCheckResult(Name, HealthStatus.Warning,
                "Conexão feita, mas nenhum computador foi encontrado nas bases de busca. Confira as OUs configuradas.",
                Duration: stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = ErrorCatalog.ActiveDirectoryUnreachable.WithDetail(ex.Message);
            return new HealthCheckResult(Name, HealthStatus.Error, error.WhatHappened, error, stopwatch.Elapsed);
        }
    }
}
