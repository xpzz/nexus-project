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
    string? UserId);

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

public interface IGraphReader
{
    IAsyncEnumerable<IntuneManagedDevice> ReadManagedDevicesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<EntraDevice> ReadEntraDevicesAsync(CancellationToken cancellationToken);
}
