using System.Runtime.CompilerServices;
using System.Net;
using Nexus.Core.Errors;

namespace Nexus.Collectors.Graph;

/// <summary>In-memory governance reads for tests and demo mode. Nothing here ever reaches a tenant.</summary>
public sealed class FakeGovernanceReader : IGraphGovernanceReader
{
    public List<AppProtectionPolicyInfo> Protection { get; init; } = [];
    public List<AppConfigInfo> Configs { get; init; } = [];
    public List<ConditionalAccessInfo> ConditionalAccess { get; init; } = [];
    public List<SignInAccess> SignIns { get; init; } = [];

    /// <summary>Simulates a tenant that did not grant the permission (or has no license) for the sign-in read.</summary>
    public bool FailSignInsWithForbidden { get; set; }

    public async IAsyncEnumerable<AppProtectionPolicyInfo> ReadAppProtectionPoliciesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var p in Protection) { await Task.Yield(); yield return p; }
    }

    public async IAsyncEnumerable<AppConfigInfo> ReadAppConfigurationsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var p in Configs) { await Task.Yield(); yield return p; }
    }

    public async IAsyncEnumerable<ConditionalAccessInfo> ReadConditionalAccessAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var p in ConditionalAccess) { await Task.Yield(); yield return p; }
    }

    public async IAsyncEnumerable<SignInAccess> ReadSignInAccessAsync(DateTimeOffset since, int maxPages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (FailSignInsWithForbidden)
        {
            throw new GraphException(HttpStatusCode.Forbidden, GraphErrors.MissingPermission("os sign-ins do Microsoft 365", "AuditLog.Read.All", "Os logs de entrada exigem Microsoft Entra ID P1 ou P2.", "/auditLogs/signIns", "simulado"));
        }

        foreach (var s in SignIns.Where(s => s.LastAccessAt >= since)) { await Task.Yield(); yield return s; }
    }
}
