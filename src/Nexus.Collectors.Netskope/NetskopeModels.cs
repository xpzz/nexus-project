namespace Nexus.Collectors.Netskope;

/// <summary>Netskope client (agent) as the tenant API reports it. The token is never part of any record.</summary>
public sealed record NetskopeClient(
    string Id,
    string? DeviceId,
    string? HostName,
    string? OperatingSystem,
    string? OsVersion,
    string? Serial,
    string? Manufacturer,
    string? Model,
    string? ClientVersion,
    string? Status,
    DateTimeOffset? LastEventAt,
    DateTimeOffset? InstalledAt,
    string? ManagementId,
    string? Users);

public interface INetskopeReader
{
    IAsyncEnumerable<NetskopeClient> ReadClientsAsync(CancellationToken cancellationToken);
}
