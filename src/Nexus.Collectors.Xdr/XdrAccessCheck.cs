using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Nexus.Core.Configuration;
using Nexus.Core.Errors;
using Nexus.Core.Health;

namespace Nexus.Collectors.Xdr;

/// <summary>Reads the XDR table with the Worker identity and reports how many agents it holds and how fresh the newest one is.</summary>
public sealed class XdrAccessCheck(XdrSettings settings, string serviceAccount, TimeProvider clock) : IHealthCheck
{
    public const string CheckName = "Cortex XDR: tabela de endpoints";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    public string Name => CheckName;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SqlServer))
        {
            return new HealthCheckResult(Name, HealthStatus.NotConfigured, "Servidor SQL do XDR não informado. Rode: nexusctl xdr-configure --server <servidor>.");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var table = XdrIdentifiers.QuotedTable(settings.Table);
            await using var connection = new SqlConnection(XdrConnectionFactory.BuildConnectionString(settings));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand($"SELECT COUNT(*), MAX(last_seen) FROM {table};", connection) { CommandTimeout = settings.CommandTimeoutSeconds };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var count = reader.GetInt32(0);
            DateTimeOffset? newest = reader.IsDBNull(1) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));

            if (count == 0)
            {
                var error = new NexusError("NEXUS-XDR-002", "A tabela do XDR está vazia.", "Nenhum agente do Cortex XDR entra no inventário.",
                    "Confirme se a rotina que carrega os endpoints do XDR (get_endpoints) está rodando e gravando nesta tabela.");
                return new HealthCheckResult(Name, HealthStatus.Warning, error.WhatHappened, error, stopwatch.Elapsed);
            }

            if (newest is { } seen && clock.GetUtcNow() - seen > StaleAfter)
            {
                var error = new NexusError("NEXUS-XDR-003", $"O agente mais recente da tabela foi visto há {(int)(clock.GetUtcNow() - seen).TotalHours} horas.",
                    "A cobertura de EDR pode estar desatualizada: a rotina que alimenta a tabela parece parada.",
                    "Verifique o agendamento da rotina do XDR e a conexão com a API do Cortex.");
                return new HealthCheckResult(Name, HealthStatus.Warning, error.WhatHappened, error, stopwatch.Elapsed);
            }

            return new HealthCheckResult(Name, HealthStatus.Ok, $"{count:N0} agentes na tabela; o mais recente visto às {newest?.ToLocalTime():HH:mm dd/MM}.", Duration: stopwatch.Elapsed);
        }
        catch (SqlException ex) when (ex.Number == 229)
        {
            var error = new NexusError("NEXUS-XDR-004", $"A conta {serviceAccount} não pode ler a tabela do XDR.", "Os agentes do Cortex XDR não entram no inventário.",
                "Peça ao DBA para rodar o script de concessão.", $"nexusctl xdr-grant-script --account {serviceAccount}").WithDetail(ex.Message);
            return new HealthCheckResult(Name, HealthStatus.Error, error.WhatHappened, error, stopwatch.Elapsed);
        }
        catch (SqlException ex) when (ex.Number is 208 or 4060 or 18456)
        {
            var error = new NexusError("NEXUS-XDR-005", ex.Number == 208 ? "A tabela do XDR não existe com esse nome." : ex.Number == 4060 ? "O banco do XDR não abriu." : "O login no SQL do XDR falhou.",
                "Os agentes do Cortex XDR não entram no inventário.",
                "Confira servidor, banco e tabela com 'nexusctl xdr-configure' e se a conta do serviço tem login no SQL.", $"nexusctl xdr-grant-script --account {serviceAccount}").WithDetail(ex.Message);
            return new HealthCheckResult(Name, HealthStatus.Error, error.WhatHappened, error, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is SqlException or ArgumentException or InvalidOperationException)
        {
            var error = new NexusError("NEXUS-XDR-001", "Não foi possível ler a tabela do XDR.", "Os agentes do Cortex XDR não entram no inventário.",
                "Verifique a conexão com o servidor SQL e a configuração (nexusctl xdr-configure).").WithDetail(ex.Message);
            return new HealthCheckResult(Name, HealthStatus.Error, error.WhatHappened, error, stopwatch.Elapsed);
        }
    }
}
