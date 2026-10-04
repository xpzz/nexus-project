namespace Nexus.Collectors.Graph;

/// <summary>An iOS or Android app protection policy with the settings that matter for data protection, the apps it targets and where it is assigned.</summary>
public sealed record AppProtectionPolicyInfo(
    string Id, string Platform, string Name, DateTimeOffset? LastModifiedAt, int? Version, bool IsAssigned, bool AssignedToAll, string Assignments,
    IReadOnlyList<string> Apps, IReadOnlyDictionary<string, string> Settings);

/// <summary>An app configuration policy (managed app or managed device). Edge's URL lists live here, not in the protection policy.</summary>
public sealed record AppConfigInfo(
    string Id, string Kind, string Platform, string Name, DateTimeOffset? LastModifiedAt, string Assignments, IReadOnlyList<string> Apps, IReadOnlyDictionary<string, string> Settings);

public sealed record ConditionalAccessInfo(
    string Id, string Name, string State, DateTimeOffset? ModifiedAt, string Users, string Applications, string Platforms, IReadOnlyList<string> GrantControls,
    bool RequiresCompliantDevice, bool RequiresApprovedApp, bool RequiresAppProtection, bool RequiresMfa, bool TargetsMicrosoft365);

/// <summary>Microsoft 365 access summarized per device (or per user, operating system and browser when the sign-in carries no device).</summary>
public sealed record SignInAccess(
    string Key, string? UserId, string? UserPrincipalName, string? EntraDeviceId, string? DeviceName, string? OperatingSystem, string? Browser, bool? IsManaged, bool? IsCompliant,
    string? TrustType, DateTimeOffset LastAccessAt, string Workloads, int Count, string? ClientApp, string? CaStatus = null);

/// <summary>Governance reads. Separate from <see cref="IGraphReader"/> so a tenant without these licenses or permissions still gets the core inventory.</summary>
public interface IGraphGovernanceReader
{
    IAsyncEnumerable<AppProtectionPolicyInfo> ReadAppProtectionPoliciesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<AppConfigInfo> ReadAppConfigurationsAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<ConditionalAccessInfo> ReadConditionalAccessAsync(CancellationToken cancellationToken);

    /// <summary>Sign-ins since the given moment, summarized per device. Needs AuditLog.Read.All and Entra ID P1.</summary>
    IAsyncEnumerable<SignInAccess> ReadSignInAccessAsync(DateTimeOffset since, int maxPages, CancellationToken cancellationToken);
}
