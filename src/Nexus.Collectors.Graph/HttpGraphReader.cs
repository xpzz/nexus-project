using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nexus.Collectors.Graph;

/// <summary>Reads Intune managed devices and Entra devices with a minimal $select (SPEC §6.2).</summary>
public sealed class HttpGraphReader(GraphHttpClient client) : IGraphReader
{
    public const string ManagedDevicesUrl = "/deviceManagement/managedDevices?$top=500&$select=id,deviceName,azureADDeviceId,serialNumber,manufacturer,model,operatingSystem,osVersion,managementAgent,deviceEnrollmentType,managedDeviceOwnerType,lastSyncDateTime,enrolledDateTime,complianceState,userPrincipalName,userId";
    public const string EntraDevicesUrl = "/devices?$top=500&$select=id,deviceId,displayName,trustType,approximateLastSignInDateTime,accountEnabled,operatingSystem,operatingSystemVersion,deviceOwnership,registrationDateTime";

    public async IAsyncEnumerable<IntuneManagedDevice> ReadManagedDevicesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var d in client.GetPagedAsync(ManagedDevicesUrl, cancellationToken))
        {
            yield return new IntuneManagedDevice(
                Text(d, "id") ?? "",
                Text(d, "deviceName"),
                ParseGuid(Text(d, "azureADDeviceId")),
                Text(d, "serialNumber"),
                Text(d, "manufacturer"),
                Text(d, "model"),
                Text(d, "operatingSystem"),
                Text(d, "osVersion"),
                Text(d, "managementAgent"),
                Text(d, "deviceEnrollmentType"),
                Text(d, "managedDeviceOwnerType"),
                Date(d, "lastSyncDateTime"),
                Date(d, "enrolledDateTime"),
                Text(d, "complianceState"),
                Text(d, "userPrincipalName"),
                Text(d, "userId"));
        }
    }

    public async IAsyncEnumerable<EntraDevice> ReadEntraDevicesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var d in client.GetPagedAsync(EntraDevicesUrl, cancellationToken))
        {
            yield return new EntraDevice(
                Text(d, "id") ?? "",
                ParseGuid(Text(d, "deviceId")),
                Text(d, "displayName"),
                Text(d, "trustType"),
                Date(d, "approximateLastSignInDateTime"),
                d.TryGetProperty("accountEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False ? enabled.GetBoolean() : null,
                Text(d, "operatingSystem"),
                Text(d, "operatingSystemVersion"),
                Text(d, "deviceOwnership"),
                Date(d, "registrationDateTime"));
        }
    }

    /// <summary>The empty GUID (00000000-...) is how Intune reports "no Entra device": it must never become an identifier.</summary>
    public static Guid? ParseGuid(string? text) => Guid.TryParse(text, out var g) && g != Guid.Empty ? g : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Text(e, name) is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1 ? d : null;
}
