using System.Diagnostics;
using Nexus.Core.Configuration;
using Nexus.Core.Errors;
using Nexus.Core.Health;

namespace Nexus.Collectors.Netskope;

/// <summary>Reads the client list with the Worker identity (and proxy) and reports how many clients came back and how fresh the newest is.</summary>
public sealed class NetskopeAccessCheck(NetskopeSettings settings, Func<INetskopeReader?> readerFactory, TimeProvider clock) : IHealthCheck
{
    public const string CheckName = "Netskope: clientes (agentes)";
    private const int SampleLimit = 5000;

    public string Name => CheckName;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Tenant))
        {
            return new HealthCheckResult(Name, HealthStatus.NotConfigured, "Tenant do Netskope não informado. Rode: nexusctl netskope-configure --tenant <tenant>.");
        }

        var stopwatch = Stopwatch.StartNew();
        INetskopeReader? reader;
        try
        {
            reader = readerFactory();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            var error = new NexusError("NEXUS-NS-010", "O token do Netskope não pôde ser lido.", "Os clientes Netskope não entram no inventário.",
                "Informe o token de novo com a mesma conta de administrador do servidor: nexusctl netskope-configure --tenant <tenant>.").WithDetail(ex.Message);
            return new HealthCheckResult(Name, HealthStatus.Error, error.WhatHappened, error, stopwatch.Elapsed);
        }

        if (reader is null)
        {
            return new HealthCheckResult(Name, HealthStatus.NotConfigured, "Token da API do Netskope não informado. Rode: nexusctl netskope-configure --tenant <tenant>.");
        }

        try
        {
            var count = 0;
            DateTimeOffset? newest = null;
            await foreach (var c in reader.ReadClientsAsync(cancellationToken))
            {
                count++;
                if (c.LastEventAt is { } at && (newest is null || at > newest))
                {
                    newest = at;
                }

                if (count >= SampleLimit)
                {
                    break;
                }
            }

            if (count == 0)
            {
                var error = new NexusError("NEXUS-NS-003", "O Netskope não devolveu nenhum cliente.", "Nenhum cliente Netskope entra no inventário.",
                    "Confirme o caminho e o tenant com 'nexusctl netskope-test'; o token pode não ter acesso aos dados de clientes.");
                return new HealthCheckResult(Name, HealthStatus.Warning, error.WhatHappened, error, stopwatch.Elapsed);
            }

            var age = newest is { } seen ? clock.GetUtcNow() - seen : (TimeSpan?)null;
            return new HealthCheckResult(Name, HealthStatus.Ok,
                $"{count:N0}{(count >= SampleLimit ? "+" : "")} clientes lidos" + (age is { } a ? $"; o evento mais recente tem {(a.TotalHours < 48 ? $"{(int)a.TotalHours} h" : $"{(int)a.TotalDays} dias")}." : "."),
                Duration: stopwatch.Elapsed);
        }
        catch (NetskopeException ex)
        {
            return new HealthCheckResult(Name, HealthStatus.Error, ex.Error.WhatHappened, ex.Error, stopwatch.Elapsed);
        }
    }
}
