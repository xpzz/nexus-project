using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nexus.Core.Errors;

namespace Nexus.Collectors.Graph;

/// <summary>Reads Intune and Entra data with a minimal $select (SPEC §6.2). Never reads IMEI, phone numbers or locations.</summary>
public sealed class HttpGraphReader(GraphHttpClient client) : IGraphReader
{
    private const string DeviceBaseFields = "id,deviceName,azureADDeviceId,serialNumber,manufacturer,model,operatingSystem,osVersion,managementAgent,deviceEnrollmentType,managedDeviceOwnerType,lastSyncDateTime,enrolledDateTime,complianceState,userPrincipalName,userId";
    public const string ManagedDevicesBaseUrl = "/deviceManagement/managedDevices?$top=500&$select=" + DeviceBaseFields;
    public const string ManagedDevicesUrl = "/deviceManagement/managedDevices?$top=500&$select=" + DeviceBaseFields +
        ",isEncrypted,jailBroken,isSupervised,totalStorageSpaceInBytes,freeStorageSpaceInBytes,physicalMemoryInBytes,deviceRegistrationState,autopilotEnrolled,complianceGracePeriodExpirationDateTime,ethernetMacAddress,wiFiMacAddress";
    public const string EntraDevicesUrl = "/devices?$top=500&$select=id,deviceId,displayName,trustType,approximateLastSignInDateTime,accountEnabled,operatingSystem,operatingSystemVersion,deviceOwnership,registrationDateTime";
    public const string MamRegistrationsUrl = "/deviceAppManagement/managedAppRegistrations?$top=100";
    public const string MamRegistrationsExpandedUrl = MamRegistrationsUrl + "&$expand=appliedPolicies($select=id,displayName),intendedPolicies($select=id,displayName)";
    public const string MamRegistrationsWithOperationsUrl = MamRegistrationsExpandedUrl + ",operations($select=displayName,lastModifiedDateTime,state)";

    private static readonly (string Kind, string Path, string NameField, bool Optional)[] PolicySources =
    [
        (PolicyKindNames.Compliance, "/deviceManagement/deviceCompliancePolicies", "displayName", false),
        (PolicyKindNames.Configuration, "/deviceManagement/deviceConfigurations", "displayName", false),
        (PolicyKindNames.SettingsCatalog, "/deviceManagement/configurationPolicies", "name", true),
        (PolicyKindNames.MamIos, "/deviceAppManagement/iosManagedAppProtections", "displayName", true),
        (PolicyKindNames.MamAndroid, "/deviceAppManagement/androidManagedAppProtections", "displayName", true),
        (PolicyKindNames.MamWindows, "/deviceAppManagement/windowsManagedAppProtections", "displayName", true),
    ];

    /// <summary>False when this tenant rejected the extended device fields and only the base fields were read.</summary>
    public bool UsedExtendedDeviceFields { get; private set; } = true;

    public async IAsyncEnumerable<IntuneManagedDevice> ReadManagedDevicesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = ManagedDevicesUrl;
        try
        {
            using var probe = await client.GetAsync(url.Replace("$top=500", "$top=1"), cancellationToken);
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            url = ManagedDevicesBaseUrl; // a field this tenant does not know: the base inventory keeps working
            UsedExtendedDeviceFields = false;
        }

        await foreach (var d in client.GetPagedAsync(url, cancellationToken))
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
                Text(d, "userId"),
                Bool(d, "isEncrypted"),
                Text(d, "jailBroken"),
                Bool(d, "isSupervised"),
                Long(d, "totalStorageSpaceInBytes"),
                Long(d, "freeStorageSpaceInBytes"),
                Long(d, "physicalMemoryInBytes"),
                Text(d, "deviceRegistrationState"),
                Bool(d, "autopilotEnrolled"),
                Date(d, "complianceGracePeriodExpirationDateTime"),
                // The MAC of a personal device is not collected: it identifies a person's own equipment (LGPD), and BYOD is matched by other keys.
                Text(d, "managedDeviceOwnerType") == "personal" ? null : Text(d, "ethernetMacAddress"),
                Text(d, "managedDeviceOwnerType") == "personal" ? null : Text(d, "wiFiMacAddress"));
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
                Bool(d, "accountEnabled"),
                Text(d, "operatingSystem"),
                Text(d, "operatingSystemVersion"),
                Text(d, "deviceOwnership"),
                Date(d, "registrationDateTime"));
        }
    }

    public async IAsyncEnumerable<IntunePolicy> ReadPoliciesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var raw = new List<(string Kind, string NameField, JsonElement Item)>();
        foreach (var (kind, path, nameField, optional) in PolicySources)
        {
            var items = await ReadPolicyKindAsync(path + "?$expand=assignments", path, optional, cancellationToken);
            raw.AddRange(items.Select(i => (kind, nameField, i)));
        }

        var groupIds = raw.SelectMany(r => Targets(r.Item)).Where(t => t.GroupId is not null).Select(t => t.GroupId!).Distinct().ToList();
        var groupNames = await ReadGroupNamesAsync(groupIds, cancellationToken);

        foreach (var (kind, nameField, item) in raw)
        {
            var targets = Targets(item).ToList();
            var parts = new List<string>();
            foreach (var t in targets)
            {
                switch (t.Type)
                {
                    case "allDevices": parts.Add("Todos os dispositivos"); break;
                    case "allLicensedUsers": parts.Add("Todos os usuários"); break;
                    case "group": parts.Add("Grupo: " + groupNames.GetValueOrDefault(t.GroupId ?? "", t.GroupId ?? "?")); break;
                    case "exclusion": parts.Add("Exclui: " + groupNames.GetValueOrDefault(t.GroupId ?? "", t.GroupId ?? "?")); break;
                }
            }

            yield return new IntunePolicy(
                kind, Text(item, "id") ?? "", Text(item, nameField) ?? Text(item, "displayName") ?? "(sem nome)", Text(item, "description"),
                PlatformOf(kind, item), Int(item, "version"), Date(item, "lastModifiedDateTime"),
                parts.Count == 0 ? "Sem atribuição" : string.Join("; ", parts.Distinct()),
                targets.Any(t => t.Type is "allDevices" or "allLicensedUsers"), targets.Count);
        }
    }

    private async Task<List<JsonElement>> ReadPolicyKindAsync(string expandedPath, string plainPath, bool optional, CancellationToken cancellationToken)
    {
        async Task<List<JsonElement>> Read(string path)
        {
            var list = new List<JsonElement>();
            await foreach (var item in client.GetPagedAsync(path, cancellationToken))
            {
                list.Add(item);
            }

            return list;
        }

        try
        {
            return await Read(expandedPath);
        }
        catch (GraphException ex) when (ex.Status is HttpStatusCode.BadRequest)
        {
            try
            {
                return await Read(plainPath); // this tenant does not allow $expand here: policies without assignments are still useful
            }
            catch (GraphException inner) when (optional && inner.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                return [];
            }
        }
        catch (GraphException ex) when (optional && ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return []; // a feature this tenant does not use or license
        }
    }

    private async Task<Dictionary<string, string>> ReadGroupNamesAsync(IReadOnlyList<string> rawGroupIds, CancellationToken cancellationToken)
    {
        var groupIds = GraphIds.Clean(rawGroupIds);
        var names = new Dictionary<string, string>();
        if (groupIds.Count == 0)
        {
            return names;
        }

        try
        {
            var responses = await client.BatchGetAsync(groupIds.Select(g => (g, $"/groups/{g}?$select=id,displayName")).ToList(), cancellationToken);
            foreach (var r in responses.Where(r => r.Status == 200 && r.Body is not null))
            {
                names[r.Id] = Text(r.Body!.Value, "displayName") ?? r.Id;
            }
        }
        catch (GraphException)
        {
            // Group names are cosmetic: show the id when the app cannot read them.
        }

        return names;
    }

    public async IAsyncEnumerable<DevicePolicyState> ReadDevicePolicyStatesAsync(IReadOnlyList<string> rawManagedDeviceIds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var managedDeviceIds = GraphIds.Clean(rawManagedDeviceIds);
        foreach (var chunk in managedDeviceIds.Chunk(100))
        {
            var requests = new List<(string, string)>(chunk.Length * 2);
            for (var i = 0; i < chunk.Length; i++)
            {
                requests.Add(($"c{i}", $"/deviceManagement/managedDevices/{chunk[i]}/deviceCompliancePolicyStates"));
                requests.Add(($"g{i}", $"/deviceManagement/managedDevices/{chunk[i]}/deviceConfigurationStates"));
            }

            var responses = await client.BatchGetAsync(requests, cancellationToken);
            var forbidden = responses.FirstOrDefault(r => r.Status == 403);
            if (forbidden is not null)
            {
                throw new GraphException(HttpStatusCode.Forbidden,
                    ErrorCatalog.Entra["Graph403"].WithDetail("/deviceManagement/managedDevices/{id}/deviceCompliancePolicyStates: permissão DeviceManagementConfiguration.Read.All ausente ou sem consentimento."));
            }

            foreach (var r in responses.Where(r => r.Status == 200 && r.Body is not null).OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                var kind = r.Id[0] == 'c' ? PolicyKindNames.Compliance : PolicyKindNames.Configuration;
                var deviceId = chunk[int.Parse(r.Id.AsSpan(1), CultureInfo.InvariantCulture)];
                if (!r.Body!.Value.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var s in value.EnumerateArray())
                {
                    yield return new DevicePolicyState(deviceId, kind, Text(s, "id"), Text(s, "displayName") ?? "(sem nome)", Text(s, "state") ?? "unknown", Text(s, "platformType"), Int(s, "settingCount"), Int(s, "version"));
                }
            }
        }
    }

    public async IAsyncEnumerable<MamRegistration> ReadMamRegistrationsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Richest read first: policy names and the selective-wipe operations. A tenant that rejects part of it still gets the rest.
        var url = MamRegistrationsWithOperationsUrl;
        foreach (var candidate in new[] { MamRegistrationsWithOperationsUrl, MamRegistrationsExpandedUrl, MamRegistrationsUrl })
        {
            url = candidate;
            try
            {
                using var probe = await client.GetAsync(candidate.Replace("$top=100", "$top=1"), cancellationToken);
                break;
            }
            catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest && candidate != MamRegistrationsUrl)
            {
                // try the next, simpler form
            }
        }

        await foreach (var r in client.GetPagedAsync(url, cancellationToken))
        {
            var type = Text(r, "@odata.type") ?? "";
            yield return new MamRegistration(
                Text(r, "id") ?? "", Text(r, "userId"), Text(r, "deviceName"), Text(r, "deviceTag"),
                type.Contains("ios", StringComparison.OrdinalIgnoreCase) ? "iOS" : type.Contains("android", StringComparison.OrdinalIgnoreCase) ? "Android" : type.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "Windows" : Text(r, "deviceType"),
                AppIdOf(r), Text(r, "applicationVersion"), Text(r, "platformVersion"), Date(r, "lastSyncDateTime"), Date(r, "createdDateTime"),
                r.TryGetProperty("flaggedReasons", out var fr) && fr.ValueKind == JsonValueKind.Array ? string.Join(", ", fr.EnumerateArray().Select(x => x.GetString())) : null,
                Names(r, "appliedPolicies"), Names(r, "intendedPolicies"), LastOperationOf(r));
        }
    }

    /// <summary>The most recent app operation of a registration (for example a selective wipe), as "name|state|when".</summary>
    private static string? LastOperationOf(JsonElement registration)
    {
        if (!registration.TryGetProperty("operations", out var ops) || ops.ValueKind != JsonValueKind.Array || ops.GetArrayLength() == 0)
        {
            return null;
        }

        var last = ops.EnumerateArray().OrderByDescending(o => Date(o, "lastModifiedDateTime") ?? DateTimeOffset.MinValue).First();
        return $"{Text(last, "displayName")}|{Text(last, "state")}|{Date(last, "lastModifiedDateTime"):O}";
    }

    public async IAsyncEnumerable<EntraUser> ReadUsersAsync(IReadOnlyList<string> rawUserIds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var userIds = GraphIds.Clean(rawUserIds);
        if (userIds.Count == 0)
        {
            yield break;
        }

        var responses = await client.BatchGetAsync(userIds.Select(u => (u, $"/users/{u}?$select=id,userPrincipalName,displayName,department,accountEnabled")).ToList(), cancellationToken);
        foreach (var r in responses.Where(r => r.Status == 200 && r.Body is not null))
        {
            var u = r.Body!.Value;
            yield return new EntraUser(Text(u, "id") ?? r.Id, Text(u, "userPrincipalName"), Text(u, "displayName"), Text(u, "department"), Bool(u, "accountEnabled"));
        }

        if (responses.Any(r => r.Status == 403))
        {
            throw new GraphException(HttpStatusCode.Forbidden, ErrorCatalog.Entra["Graph403"].WithDetail("/users/{id}: permissão User.Read.All ausente ou sem consentimento."));
        }
    }

    public async Task<IReadOnlyList<DetectedApp>> ReadDetectedAppsAsync(string managedDeviceId, CancellationToken cancellationToken)
    {
        var apps = new List<DetectedApp>();
        await foreach (var a in client.GetPagedAsync($"/deviceManagement/managedDevices/{managedDeviceId}/detectedApps?$top=200", cancellationToken))
        {
            apps.Add(new DetectedApp(Text(a, "displayName") ?? "(sem nome)", Text(a, "version"), Text(a, "publisher"), Long(a, "sizeInByte")));
        }

        return apps;
    }

    private readonly record struct Target(string Type, string? GroupId);

    private static IEnumerable<Target> Targets(JsonElement policy)
    {
        if (!policy.TryGetProperty("assignments", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var a in list.EnumerateArray())
        {
            if (!a.TryGetProperty("target", out var t))
            {
                continue;
            }

            var type = Text(t, "@odata.type") ?? "";
            yield return type switch
            {
                _ when type.Contains("allDevices", StringComparison.OrdinalIgnoreCase) => new Target("allDevices", null),
                _ when type.Contains("allLicensedUsers", StringComparison.OrdinalIgnoreCase) => new Target("allLicensedUsers", null),
                _ when type.Contains("exclusion", StringComparison.OrdinalIgnoreCase) => new Target("exclusion", Text(t, "groupId")),
                _ => new Target("group", Text(t, "groupId")),
            };
        }
    }

    private static string? PlatformOf(string kind, JsonElement item)
    {
        switch (kind)
        {
            case PolicyKindNames.MamIos: return "iOS";
            case PolicyKindNames.MamAndroid: return "Android";
            case PolicyKindNames.MamWindows: return "Windows";
        }

        if (Text(item, "platforms") is { } platforms)
        {
            return platforms;
        }

        var type = Text(item, "@odata.type") ?? "";
        foreach (var (needle, label) in new[] { ("ios", "iOS"), ("android", "Android"), ("macos", "macOS"), ("windows", "Windows"), ("aosp", "Android") })
        {
            if (type.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }
        }

        return null;
    }

    private static string? AppIdOf(JsonElement registration)
    {
        if (!registration.TryGetProperty("appIdentifier", out var id) || id.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return Text(id, "bundleId") ?? Text(id, "packageId") ?? Text(id, "windowsAppId") ?? Text(id, "windowsAppId");
    }

    private static string? Names(JsonElement e, string property) =>
        e.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            ? string.Join("; ", list.EnumerateArray().Select(p => Text(p, "displayName") ?? Text(p, "id")).Where(n => n is not null))
            : null;

    /// <summary>The empty GUID (00000000-...) is how Intune reports "no Entra device": it must never become an identifier.</summary>
    public static Guid? ParseGuid(string? text) => Guid.TryParse(text, out var g) && g != Guid.Empty ? g : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Text(e, name) is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1 ? d : null;
}

/// <summary>Kind identifiers shared with the stored policy records (kept here so the collector has no dependency on the data layer).</summary>
public static class PolicyKindNames
{
    public const string Compliance = "compliance", Configuration = "configuration", SettingsCatalog = "settings",
        MamIos = "mam-ios", MamAndroid = "mam-android", MamWindows = "mam-windows";
}
