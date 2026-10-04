namespace Nexus.Data.Entities;

public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
}

public static class CommandTypes
{
    public const string RunHealthChecks = "RunHealthChecks";
    public const string CollectNow = "CollectNow";
    public const string PauseCollectors = "PauseCollectors";
    public const string ResumeCollectors = "ResumeCollectors";
    /// <summary>Argument: asset id. Reads installed software for that device from SCCM and Intune.</summary>
    public const string FetchInventory = "FetchInventory";
}

public enum CommandStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>Web and CLI talk to the Worker only through this table: no extra ports (SPEC §5.2).</summary>
public sealed class WorkerCommand
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string? Argument { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public CommandStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Result { get; set; }
}

public sealed class HealthResultRecord
{
    public long Id { get; set; }
    /// <summary>Groups the results of one execution of all checks.</summary>
    public Guid RunId { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string? ErrorCode { get; set; }
    public string? Impact { get; set; }
    public string? HowToFix { get; set; }
    public string? Script { get; set; }
    public int DurationMs { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    /// <summary>Identity that executed the check (SPEC premise 5).</summary>
    public string ExecutedAs { get; set; } = "";
}

public sealed class JobState
{
    public string Name { get; set; } = "";
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? LastCompletedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastStatus { get; set; }
    public int? LastDurationMs { get; set; }
    public int? LastRecordCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}

/// <summary>Singleton row (Id = 1) with the setup mode state (SPEC §4.1).</summary>
public sealed class SetupState
{
    public int Id { get; set; } = 1;
    public bool SetupModeActive { get; set; } = true;
    public string? CodeHash { get; set; }
    public DateTimeOffset? CodeExpiresAt { get; set; }
    public DateTimeOffset? CodeUsedAt { get; set; }
}

/// <summary>Raw record of v_R_System as read in the last successful collection.</summary>
public sealed class SccmDeviceRecord
{
    public int ResourceId { get; set; }
    public string? Name { get; set; }
    public string? Domain { get; set; }
    public bool? Client { get; set; }
    public bool? Active { get; set; }
    public bool? Obsolete { get; set; }
    public Guid? AadDeviceId { get; set; }
    public string? SmbiosGuid { get; set; }
    public string? OperatingSystem { get; set; }
    public string? Serial { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset? LastActiveAt { get; set; }
    public int? ClientActiveStatus { get; set; }
    public string? ClientVersion { get; set; }
    public DateTimeOffset? LastPolicyRequestAt { get; set; }
    public DateTimeOffset? LastHwScanAt { get; set; }
    public DateTimeOffset? LastSwScanAt { get; set; }
    public DateTimeOffset? LastDdrAt { get; set; }
    public string? LastLogonUser { get; set; }
    public string? AdSite { get; set; }
    public string? OsVersion { get; set; }
    public DateTimeOffset? LastBootAt { get; set; }
    public string? CpuName { get; set; }
    public int? CpuCores { get; set; }
    public long? MemoryMb { get; set; }
    public long? DiskTotalMb { get; set; }
    public long? DiskFreeMb { get; set; }
    public string? BiosVersion { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
}

/// <summary>Raw AD computer object as read in the last successful collection.</summary>
public sealed class AdComputerRecord
{
    public Guid ObjectGuid { get; set; }
    public string Name { get; set; } = "";
    public string? DnsHostName { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OperatingSystemVersion { get; set; }
    public DateTimeOffset? LastLogonTimestamp { get; set; }
    public DateTimeOffset? PasswordLastSet { get; set; }
    public DateTimeOffset? WhenCreated { get; set; }
    public bool Enabled { get; set; }
    public string DistinguishedName { get; set; } = "";
    public DateTimeOffset CollectedAt { get; set; }
}

/// <summary>Intune managedDevice as read in the last successful collection.</summary>
public sealed class IntuneDeviceRecord
{
    public string Id { get; set; } = "";
    public string? DeviceName { get; set; }
    public Guid? AzureAdDeviceId { get; set; }
    public string? SerialNumber { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? ManagementAgent { get; set; }
    public string? EnrollmentType { get; set; }
    public string? OwnerType { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset? EnrolledAt { get; set; }
    public string? ComplianceState { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? UserId { get; set; }
    public bool? IsEncrypted { get; set; }
    /// <summary>Graph reports "True", "False" or "Unknown".</summary>
    public string? JailBroken { get; set; }
    public bool? IsSupervised { get; set; }
    public long? TotalStorageBytes { get; set; }
    public long? FreeStorageBytes { get; set; }
    public long? PhysicalMemoryBytes { get; set; }
    public string? DeviceRegistrationState { get; set; }
    public bool? AutopilotEnrolled { get; set; }
    public DateTimeOffset? ComplianceGraceExpiresAt { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
}

/// <summary>Cortex XDR endpoint as read from the site's table in the last successful collection.</summary>
public sealed class XdrEndpointRecord
{
    public string AgentId { get; set; } = "";
    public string? HostName { get; set; }
    public string? AgentStatus { get; set; }
    public string? OperationalStatus { get; set; }
    public string? AgentType { get; set; }
    public string? Ip { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? Users { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
}

/// <summary>Netskope client (agent) as read in the last successful collection.</summary>
public sealed class NetskopeClientRecord
{
    public string Id { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? HostName { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? Serial { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? ClientVersion { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public DateTimeOffset? InstalledAt { get; set; }
    /// <summary>Identifier the management tool gave the device (an Entra or Intune id when Netskope is integrated with them).</summary>
    public string? ManagementId { get; set; }
    public string? Users { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
}

/// <summary>Entra user behind a device or a MAM registration: department and whether the account is enabled.</summary>
public sealed class EntraUserRecord
{
    public string Id { get; set; } = "";
    public string? UserPrincipalName { get; set; }
    public string? DisplayName { get; set; }
    public string? Department { get; set; }
    public bool? AccountEnabled { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
}

public static class PolicyKinds
{
    public const string Compliance = "compliance", Configuration = "configuration", SettingsCatalog = "settings",
        MamIos = "mam-ios", MamAndroid = "mam-android", MamWindows = "mam-windows";

    public static string Title(string kind) => kind switch
    {
        Compliance => "Conformidade",
        Configuration => "Perfil de configuração",
        SettingsCatalog => "Catálogo de configurações",
        MamIos => "Proteção de apps (iOS)",
        MamAndroid => "Proteção de apps (Android)",
        MamWindows => "Proteção de apps (Windows)",
        _ => kind,
    };

    public static bool IsMam(string kind) => kind.StartsWith("mam-", StringComparison.Ordinal);
}

/// <summary>Intune policy or profile (compliance, configuration, settings catalog, app protection) with where it is assigned.</summary>
public sealed class IntunePolicyRecord
{
    /// <summary>"kind:id", unique across kinds.</summary>
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string PolicyId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Platform { get; set; }
    public int? Version { get; set; }
    public DateTimeOffset? LastModifiedAt { get; set; }
    /// <summary>Readable summary: "Todos os dispositivos; Grupo: X; Exclui: Y".</summary>
    public string? Assignments { get; set; }
    public bool AssignedToAll { get; set; }
    public int AssignmentCount { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
}

/// <summary>State of one policy on one device (compliance policy or configuration profile), as Intune reports it.</summary>
public sealed class IntuneDevicePolicyState
{
    public long Id { get; set; }
    public string IntuneDeviceId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? PolicyId { get; set; }
    public string PolicyName { get; set; } = "";
    /// <summary>compliant, nonCompliant, error, conflict, notApplicable, pending, unknown...</summary>
    public string State { get; set; } = "";
    public string? Platform { get; set; }
    public int? SettingCount { get; set; }
    public int? Version { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
}

/// <summary>App protection (MAM) registration: one app instance of one user on one device, with the policies applied.</summary>
public sealed class MamRegistrationRecord
{
    public string Id { get; set; } = "";
    public string? UserId { get; set; }
    public string? DeviceName { get; set; }
    public string? DeviceTag { get; set; }
    /// <summary>iOS, Android or Windows.</summary>
    public string? DeviceType { get; set; }
    public string? AppIdentifier { get; set; }
    public string? AppVersion { get; set; }
    public string? PlatformVersion { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? FlaggedReasons { get; set; }
    public string? AppliedPolicies { get; set; }
    public string? IntendedPolicies { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
}

/// <summary>Software found on one device, fetched on demand when someone opens the device (too large to collect for the whole estate).</summary>
public sealed class InstalledSoftwareRecord
{
    public long Id { get; set; }
    public Guid AssetId { get; set; }
    /// <summary>sccm or intune.</summary>
    public string Source { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public DateTimeOffset? InstalledOn { get; set; }
    public long? SizeBytes { get; set; }
}

public sealed class InventoryFetch
{
    public Guid AssetId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
    /// <summary>Pending, Done or Failed.</summary>
    public string Status { get; set; } = "Pending";
    public string? Message { get; set; }
}

/// <summary>Entra device as read in the last successful collection.</summary>
public sealed class EntraDeviceRecord
{
    public string Id { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public string? DisplayName { get; set; }
    public string? TrustType { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }
    public bool? AccountEnabled { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OperatingSystemVersion { get; set; }
    public string? Ownership { get; set; }
    public DateTimeOffset? RegisteredAt { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
}

/// <summary>
/// Reconciled device (SPEC §7). Management channels are independent facts, never merged into one state:
/// an Entra-registered device is not Intune-enrolled, and tenant attach is not MDM.
/// </summary>
public sealed class Asset
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Serial { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    /// <summary>WindowsClient, WindowsServer, Android, iOS, macOS or Other.</summary>
    public string Platform { get; set; } = "Other";
    /// <summary>Operating system name as reported by the most reliable source (e.g. "Windows 10 Enterprise").</summary>
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    /// <summary>Corporate, Personal or Unknown (value declared by the source; see <see cref="OwnershipSource"/>).</summary>
    public string Ownership { get; set; } = "Unknown";
    public string? OwnershipSource { get; set; }
    public string? Department { get; set; }
    public string? PrimaryUser { get; set; }

    public bool InSccm { get; set; }
    /// <summary>Healthy, Inactive, NoClient, Obsolete or NotApplicable.</summary>
    public string SccmHealth { get; set; } = "NotApplicable";
    public bool SccmClient { get; set; }
    public bool InIntune { get; set; }
    /// <summary>Mdm, TenantAttach, SecurityManagement, Other or None.</summary>
    public string IntuneChannel { get; set; } = "None";
    public string? ComplianceState { get; set; }
    public bool InEntra { get; set; }
    public string? EntraTrustType { get; set; }
    public bool InAd { get; set; }
    public bool AdEnabled { get; set; } = true;
    /// <summary>The AD record was attached by name only (supporting evidence, low confidence).</summary>
    public bool AdByNameOnly { get; set; }

    public DateTimeOffset? SccmLastSeenAt { get; set; }
    public DateTimeOffset? IntuneLastSyncAt { get; set; }
    public DateTimeOffset? EntraLastSignInAt { get; set; }
    public DateTimeOffset? AdLastLogonAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Both, OnlySccm, OnlyIntune or Neither (client and MDM are the channels counted).</summary>
    public string Coverage { get; set; } = "Neither";
    /// <summary>High, Medium or Low: the weakest evidence that joined records into this asset.</summary>
    public string Confidence { get; set; } = "High";
    public bool NeedsReview { get; set; }

    public bool InNetskope { get; set; }
    public bool NetskopeByNameOnly { get; set; }
    public string? NetskopeStatus { get; set; }
    public string? NetskopeVersion { get; set; }
    public DateTimeOffset? NetskopeLastSeenAt { get; set; }

    /// <summary>Confirmed, Single, Unconfirmed or Inactive (see ADR-0006).</summary>
    public string ActivityClass { get; set; } = "Inactive";
    /// <summary>How many tools reported inside the activity window.</summary>
    public int ActiveSourceCount { get; set; }
    /// <summary>Tools that reported inside the window, e.g. "sccm,xdr,netskope".</summary>
    public string? ActiveSources { get; set; }
    /// <summary>Latest report from a tool that runs on the device itself (agent, client or app), as opposed to the identity side.</summary>
    public DateTimeOffset? LastStrongActivityAt { get; set; }

    public bool InXdr { get; set; }
    /// <summary>The XDR record was attached by name only (supporting evidence, low confidence).</summary>
    public bool XdrByNameOnly { get; set; }
    public string? XdrStatus { get; set; }
    public string? XdrOperationalStatus { get; set; }
    public string? XdrAgentType { get; set; }
    public string? XdrIp { get; set; }
    public DateTimeOffset? XdrLastSeenAt { get; set; }
    public string? IntuneUserId { get; set; }
    public bool? UserEnabled { get; set; }
    public bool? IsEncrypted { get; set; }
    public bool? JailBroken { get; set; }
    public bool HasMam { get; set; }
    public int MamAppCount { get; set; }
    public DateTimeOffset? MamLastSyncAt { get; set; }
    public string? MamPolicies { get; set; }
    public bool PoliciesCollected { get; set; }
    public int CompliancePolicies { get; set; }
    public int CompliancePoliciesFailed { get; set; }
    public int ConfigProfiles { get; set; }
    public int ConfigProfilesFailed { get; set; }
    public DateTimeOffset? SccmLastPolicyAt { get; set; }
    public DateTimeOffset? SccmLastHwScanAt { get; set; }
    public string? SccmClientVersion { get; set; }
    public long? MemoryMb { get; set; }
    public long? DiskTotalMb { get; set; }
    public long? DiskFreeMb { get; set; }
    public string? CpuName { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AssetLink
{
    public long Id { get; set; }
    public Guid AssetId { get; set; }
    /// <summary>sccm, intune, entra or ad.</summary>
    public string Source { get; set; } = "";
    public string SourceKey { get; set; } = "";
    /// <summary>aad-device-id, serial, hardware-uuid, name or none.</summary>
    public string Evidence { get; set; } = "none";
    public string Confidence { get; set; } = "High";
    public string? Reason { get; set; }
}

/// <summary>Ambiguous correlation waiting for a person (SPEC §7.3). Never counted as one device.</summary>
public sealed class ReviewItem
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Sources { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
