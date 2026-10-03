namespace Nexus.Collectors.ActiveDirectory;

public sealed record AdComputer(
    Guid ObjectGuid,
    string Name,
    string? DnsHostName,
    string? OperatingSystem,
    string? OperatingSystemVersion,
    DateTimeOffset? LastLogonTimestamp,
    DateTimeOffset? PasswordLastSet,
    DateTimeOffset? WhenCreated,
    bool Enabled,
    string DistinguishedName);

/// <summary>Access to AD behind an interface so tests and demo mode use an in-memory fake (SPEC §13).</summary>
public interface IDirectoryReader
{
    IAsyncEnumerable<AdComputer> ReadComputersAsync(CancellationToken cancellationToken);
}

public static class AdAttributes
{
    /// <summary>Only the attributes listed in SPEC §6.3.</summary>
    public static readonly string[] Computer =
    [
        "name", "dNSHostName", "operatingSystem", "operatingSystemVersion", "lastLogonTimestamp",
        "pwdLastSet", "whenCreated", "userAccountControl", "distinguishedName", "objectGUID",
        "objectSid", "managedBy", "description",
    ];

    public const int AccountDisable = 0x2;
}
