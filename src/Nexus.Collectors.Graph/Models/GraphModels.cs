namespace Nexus.Collectors.Graph;

/// <summary>Intune managedDevice as read from Microsoft Graph (v1.0). Presence here does not prove MDM enrollment.</summary>
public sealed record IntuneManagedDevice(
    string Id,
    string? DeviceName,
    Guid? AzureAdDeviceId,
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    string? OperatingSystem,
    string? OsVersion,
    string? ManagementAgent,
    string? EnrollmentType,
    string? OwnerType,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset? EnrolledAt,
    string? ComplianceState,
    string? UserPrincipalName,
    string? UserId,
    bool? IsEncrypted = null,
    string? JailBroken = null,
    bool? IsSupervised = null,
    long? TotalStorageBytes = null,
    long? FreeStorageBytes = null,
    long? PhysicalMemoryBytes = null,
    string? DeviceRegistrationState = null,
    bool? AutopilotEnrolled = null,
    DateTimeOffset? ComplianceGraceExpiresAt = null);

/// <summary>Microsoft Entra device. <see cref="DeviceId"/> equals the Intune azureADDeviceId (not the directory object id).</summary>
public sealed record EntraDevice(
    string Id,
    Guid? DeviceId,
    string? DisplayName,
    string? TrustType,
    DateTimeOffset? LastSignInAt,
    bool? AccountEnabled,
    string? OperatingSystem,
    string? OperatingSystemVersion,
    string? Ownership,
    DateTimeOffset? RegisteredAt);

/// <summary>Intune policy or profile with a readable summary of where it is assigned.</summary>
public sealed record IntunePolicy(
    string Kind, string Id, string Name, string? Description, string? Platform, int? Version, DateTimeOffset? LastModifiedAt,
    string? Assignments, bool AssignedToAll, int AssignmentCount);

/// <summary>State of one policy on one device.</summary>
public sealed record DevicePolicyState(
    string ManagedDeviceId, string Kind, string? PolicyId, string PolicyName, string State, string? Platform, int? SettingCount, int? Version);

public sealed record MamRegistration(
    string Id, string? UserId, string? DeviceName, string? DeviceTag, string? DeviceType, string? AppIdentifier, string? AppVersion,
    string? PlatformVersion, DateTimeOffset? LastSyncAt, DateTimeOffset? CreatedAt, string? FlaggedReasons, string? AppliedPolicies, string? IntendedPolicies);

public sealed record EntraUser(string Id, string? UserPrincipalName, string? DisplayName, string? Department, bool? AccountEnabled);

public sealed record DetectedApp(string Name, string? Version, string? Publisher, long? SizeBytes);

public interface IGraphReader
{
    /// <summary>Compliance policies, configuration profiles, settings catalog and app protection policies, with assignments.</summary>
    IAsyncEnumerable<IntunePolicy> ReadPoliciesAsync(CancellationToken cancellationToken);

    /// <summary>Policy and profile states for the given Intune devices (batched).</summary>
    IAsyncEnumerable<DevicePolicyState> ReadDevicePolicyStatesAsync(IReadOnlyList<string> managedDeviceIds, CancellationToken cancellationToken);

    IAsyncEnumerable<MamRegistration> ReadMamRegistrationsAsync(CancellationToken cancellationToken);

    /// <summary>Department and account state for the given Entra user ids (batched).</summary>
    IAsyncEnumerable<EntraUser> ReadUsersAsync(IReadOnlyList<string> userIds, CancellationToken cancellationToken);

    /// <summary>Applications Intune detected on one device (on demand).</summary>
    Task<IReadOnlyList<DetectedApp>> ReadDetectedAppsAsync(string managedDeviceId, CancellationToken cancellationToken);

    IAsyncEnumerable<IntuneManagedDevice> ReadManagedDevicesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<EntraDevice> ReadEntraDevicesAsync(CancellationToken cancellationToken);
}
