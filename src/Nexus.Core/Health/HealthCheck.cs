using System.Text.Json.Serialization;
using Nexus.Core.Errors;

namespace Nexus.Core.Health;

[JsonConverter(typeof(JsonStringEnumConverter<HealthStatus>))]
public enum HealthStatus
{
    Ok,
    Warning,
    Error,
    NotConfigured,
    NotEnabled,
}

public sealed record HealthCheckResult(
    string Name,
    HealthStatus Status,
    string Message,
    NexusError? Error = null,
    TimeSpan Duration = default)
{
    public static HealthCheckResult Ok(string name, string message) => new(name, HealthStatus.Ok, message);

    public static HealthCheckResult NotConfigured(string name, NexusError error) =>
        new(name, HealthStatus.NotConfigured, error.WhatHappened, error);

    public static HealthCheckResult Failed(string name, NexusError error) =>
        new(name, HealthStatus.Error, error.WhatHappened, error);
}

/// <summary>
/// Access checks always run inside the Worker service, with the service identity (SPEC premise 5).
/// </summary>
public interface IHealthCheck
{
    string Name { get; }
    Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken);
}
