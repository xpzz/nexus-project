using Nexus.Core;
using Nexus.Collectors.Graph;
using Nexus.Core.Azure;
using Nexus.Core.Configuration;
using Nexus.Core.Errors;
using Nexus.Core.Health;
using Nexus.Worker.Collection;

namespace Nexus.Worker.Health;

/// <summary>
/// Checks the Azure access as the collector app (certificate), not the service account: token, the "roles" claim
/// against the required permissions, and one real read per permission (SPEC §4.8).
/// </summary>
public sealed class GraphAccessCheck(NexusSettings settings, NexusPaths paths, ISourceFactory sources) : IHealthCheck
{
    public const string CheckName = "Azure: Entra ID e Intune";
    private const int ActivePhase = 2; // policies, MAM and users are read now

    public string Name => CheckName;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (settings.DemoMode)
        {
            return HealthCheckResult.Ok(Name, "Fonte simulada (modo demonstração).");
        }

        if (AzureSettingsStore.Load(paths) is null)
        {
            return new HealthCheckResult(Name, HealthStatus.NotConfigured,
                "Registros de aplicativo pendentes. Conclua a etapa Azure no assistente (Configurar-Azure.cmd).");
        }

        GraphConnection? connection;
        try
        {
            connection = sources.CreateGraphConnection(settings);
        }
        catch (InvalidOperationException ex)
        {
            var error = new NexusError("NEXUS-AZ-010", "O certificado do aplicativo coletor não pôde ser usado.",
                "A coleta do Intune e do Entra ID não roda.", "Rode Configurar-Azure.cmd de novo no servidor.").WithDetail(ex.Message);
            return HealthCheckResult.Failed(Name, error);
        }

        if (connection is null)
        {
            return new HealthCheckResult(Name, HealthStatus.NotConfigured, "Azure ainda não configurado neste servidor.");
        }

        try
        {
            var token = await connection.Tokens.GetTokenAsync(cancellationToken);
            var required = GraphPermissions.RequiredUpTo(ActivePhase);
            var missing = GraphPermissions.Missing(GraphAssertion.Roles(token), required);
            if (missing.Count > 0)
            {
                var error = new NexusError("NEXUS-AZ-020", $"Faltam permissões no aplicativo coletor: {string.Join(", ", missing.Select(p => p.Name))}.",
                    "Os módulos que dependem delas ficam indisponíveis.",
                    "Peça a um administrador global para conceder o consentimento (Configurar-Azure.cmd) e aguarde alguns minutos.");
                return new HealthCheckResult(Name, HealthStatus.Warning, error.WhatHappened, error);
            }

            var failed = new List<string>();
            foreach (var permission in required.Where(p => p.Probe is not null))
            {
                try
                {
                    using var _ = await connection.Client.GetAsync(permission.Probe!, cancellationToken);
                }
                catch (GraphException ex)
                {
                    failed.Add($"{permission.Name} ({(int)ex.Status})");
                }
            }

            return failed.Count == 0
                ? HealthCheckResult.Ok(Name, $"Token obtido e {required.Count} permissões de leitura verificadas.")
                : new HealthCheckResult(Name, HealthStatus.Warning, $"Leitura recusada: {string.Join(", ", failed)}.",
                    ErrorCatalog.Entra["Graph403"].WithDetail(string.Join(", ", failed)));
        }
        catch (GraphException ex)
        {
            return HealthCheckResult.Failed(Name, ex.Error);
        }
    }
}
