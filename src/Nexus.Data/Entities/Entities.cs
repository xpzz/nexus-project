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
