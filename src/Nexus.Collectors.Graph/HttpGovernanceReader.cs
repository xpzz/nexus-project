using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nexus.Core.Errors;

namespace Nexus.Collectors.Graph;

/// <summary>
/// Reads what governs corporate data on personal and corporate devices: app protection policies and their settings, app configuration (Edge URL lists),
/// Conditional Access and Microsoft 365 sign-ins. Read-only. Every piece is optional: a tenant without the license or the permission gets an explained
/// error for that piece only.
/// </summary>
public sealed class HttpGovernanceReader(GraphHttpClient client) : IGraphGovernanceReader
{
    private static readonly (string Platform, string Path)[] ProtectionSources =
    [
        ("iOS", "/deviceAppManagement/iosManagedAppProtections"),
        ("Android", "/deviceAppManagement/androidManagedAppProtections"),
    ];

    /// <summary>The settings that decide whether corporate data can leave the app. Everything else on the policy is left out on purpose.</summary>
    public static readonly string[] ProtectionSettingNames =
    [
        "allowedInboundDataTransferSources", "allowedOutboundDataTransferDestinations", "allowedOutboundClipboardSharingLevel", "allowedDataStorageLocations",
        "dataBackupBlocked", "pinRequired", "minimumPinLength", "pinCharacterSet", "simplePinBlocked", "fingerprintBlocked", "biometricAuthenticationBlocked",
        "organizationalCredentialsRequired", "contactSyncBlocked", "printBlocked", "saveAsBlocked", "managedBrowserToOpenLinksRequired", "managedBrowser",
        "appDataEncryptionType", "encryptAppData", "screenCaptureBlocked", "periodOfflineBeforeWipeIsEnforced", "periodOfflineBeforeAccessCheck",
        "disableAppPinIfDevicePinIsSet", "minimumRequiredOsVersion", "minimumWarningOsVersion", "minimumRequiredAppVersion", "deviceComplianceRequired",
    ];

    private static readonly string[] Workloads = ["Exchange", "SharePoint", "Teams", "OneDrive"];

    public async IAsyncEnumerable<AppProtectionPolicyInfo> ReadAppProtectionPoliciesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var list = new List<AppProtectionPolicyInfo>();
        try
        {
            await foreach (var item in ReadAppProtectionPoliciesCore(cancellationToken))
            {
                list.Add(item);
            }
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.Forbidden)
        {
            throw new GraphException(ex.Status, GraphErrors.MissingPermission("as políticas de proteção de aplicativos", "DeviceManagementApps.Read.All", null, "ReadAppProtectionPoliciesAsync", ex.Error.WhatHappened));
        }

        foreach (var item in list)
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<AppProtectionPolicyInfo> ReadAppProtectionPoliciesCore([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var (platform, path) in ProtectionSources)
        {
            var policies = await ReadOptionalAsync(path + "?$top=100", cancellationToken);
            if (policies.Count == 0)
            {
                continue;
            }

            var ids = GraphIds.Clean(policies.Select(p => Text(p, "id")));
            var details = await ReadChildrenAsync(path, ids, ["apps", "assignments"], cancellationToken);
            var groupNames = await ReadGroupNamesAsync(details.Values.SelectMany(d => d.GetValueOrDefault("assignments") ?? []).SelectMany(a => GroupIdsOf(a)).ToList(), cancellationToken);

            foreach (var p in policies)
            {
                var id = Text(p, "id") ?? "";
                var child = GraphIds.TryNormalize(id, out var normalized) ? details.GetValueOrDefault(normalized) : null;
                var (summary, all, assigned) = Summarize(child?.GetValueOrDefault("assignments") ?? [], groupNames);
                var apps = (child?.GetValueOrDefault("apps") ?? []).Select(AppIdentifier).Where(a => a is not null).Select(a => a!).Distinct().ToList();
                yield return new AppProtectionPolicyInfo(id, platform, Text(p, "displayName") ?? "(sem nome)", Date(p, "lastModifiedDateTime"), Int(p, "version") ?? VersionOf(p),
                    assigned, all, summary, apps, Settings(p, ProtectionSettingNames));
            }
        }
    }

    public async IAsyncEnumerable<AppConfigInfo> ReadAppConfigurationsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var list = new List<AppConfigInfo>();
        try
        {
            await foreach (var item in ReadAppConfigurationsCore(cancellationToken))
            {
                list.Add(item);
            }
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.Forbidden)
        {
            throw new GraphException(ex.Status, GraphErrors.MissingPermission("as configurações de aplicativos (Edge)", "DeviceManagementApps.Read.All", null, "ReadAppConfigurationsAsync", ex.Error.WhatHappened));
        }

        foreach (var item in list)
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<AppConfigInfo> ReadAppConfigurationsCore([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 1. Managed app configuration (MAM): custom settings are name/value pairs.
        var targeted = await ReadOptionalAsync("/deviceAppManagement/targetedManagedAppConfigurations?$top=100", cancellationToken);
        var ids = GraphIds.Clean(targeted.Select(p => Text(p, "id")));
        var details = await ReadChildrenAsync("/deviceAppManagement/targetedManagedAppConfigurations", ids, ["apps", "assignments"], cancellationToken);
        var names = await ReadGroupNamesAsync(details.Values.SelectMany(d => d.GetValueOrDefault("assignments") ?? []).SelectMany(a => GroupIdsOf(a)).ToList(), cancellationToken);
        foreach (var p in targeted)
        {
            var id = Text(p, "id") ?? "";
            var child = GraphIds.TryNormalize(id, out var normalized) ? details.GetValueOrDefault(normalized) : null;
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (p.TryGetProperty("customSettings", out var custom) && custom.ValueKind == JsonValueKind.Array)
            {
                foreach (var kv in custom.EnumerateArray())
                {
                    if (Text(kv, "name") is { Length: > 0 } key)
                    {
                        settings[key] = Text(kv, "value") ?? "";
                    }
                }
            }

            var apps = (child?.GetValueOrDefault("apps") ?? []).Select(AppIdentifier).Where(a => a is not null).Select(a => a!).Distinct().ToList();
            var (summary, _, _) = Summarize(child?.GetValueOrDefault("assignments") ?? [], names);
            yield return new AppConfigInfo(id, "managed-app", PlatformFromApps(apps), Text(p, "displayName") ?? "(sem nome)", Date(p, "lastModifiedDateTime"), summary, apps, settings);
        }

        // 2. Managed device app configuration (MDM): iOS key/value settings or Android Enterprise managed configuration (payloadJson).
        var devices = await ReadOptionalAsync("/deviceAppManagement/mobileAppConfigurations?$expand=assignments&$top=100", cancellationToken);
        var deviceGroups = await ReadGroupNamesAsync(devices.SelectMany(GroupIdsOfConfig).ToList(), cancellationToken);
        foreach (var p in devices)
        {
            var type = Text(p, "@odata.type") ?? "";
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (p.TryGetProperty("settings", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var kv in items.EnumerateArray())
                {
                    if (Text(kv, "appConfigKey") is { Length: > 0 } key)
                    {
                        settings[key] = Text(kv, "appConfigKeyValue") ?? "";
                    }
                }
            }

            if (Text(p, "payloadJson") is { Length: > 0 } payload)
            {
                foreach (var (key, value) in ParsePayload(payload))
                {
                    settings[key] = value;
                }
            }

            var (summary, _, _) = Summarize(AssignmentsOf(p), deviceGroups);
            var platform = type.Contains("ios", StringComparison.OrdinalIgnoreCase) ? "iOS" : type.Contains("android", StringComparison.OrdinalIgnoreCase) ? "Android" : "Outro";
            yield return new AppConfigInfo(Text(p, "id") ?? "", "managed-device", platform, Text(p, "displayName") ?? "(sem nome)", Date(p, "lastModifiedDateTime"), summary, [], settings);
        }
    }

    public async IAsyncEnumerable<ConditionalAccessInfo> ReadConditionalAccessAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var list = new List<ConditionalAccessInfo>();
        try
        {
            await foreach (var item in ReadConditionalAccessCore(cancellationToken))
            {
                list.Add(item);
            }
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.Forbidden)
        {
            throw new GraphException(ex.Status, GraphErrors.MissingPermission("as políticas de Acesso Condicional", "Policy.Read.All", "O Acesso Condicional exige Microsoft Entra ID P1.", "ReadConditionalAccessAsync", ex.Error.WhatHappened));
        }

        foreach (var item in list)
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<ConditionalAccessInfo> ReadConditionalAccessCore([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var p in client.GetPagedAsync("/identity/conditionalAccess/policies", cancellationToken))
        {
            var conditions = p.TryGetProperty("conditions", out var c) ? c : default;
            var grants = p.TryGetProperty("grantControls", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
            var builtIn = grants.ValueKind == JsonValueKind.Object ? Strings(grants, "builtInControls") : [];
            var apps = Include(conditions, "applications", "includeApplications");
            var users = Include(conditions, "users", "includeUsers");
            var groups = conditions.ValueKind == JsonValueKind.Object && conditions.TryGetProperty("users", out var u) ? Strings(u, "includeGroups").Count : 0;
            var platforms = Include(conditions, "platforms", "includePlatforms");
            yield return new ConditionalAccessInfo(
                Text(p, "id") ?? "", Text(p, "displayName") ?? "(sem nome)", Text(p, "state") ?? "disabled", Date(p, "modifiedDateTime") ?? Date(p, "createdDateTime"),
                users.Contains("All") ? "Todos os usuários" : groups > 0 ? $"{groups} grupo(s)" + (users.Count > 0 ? $" e {users.Count} usuário(s)" : "") : users.Count > 0 ? $"{users.Count} usuário(s)" : "—",
                apps.Contains("All") ? "Todos os aplicativos" : apps.Contains("Office365") ? "Microsoft 365" : apps.Count == 0 ? "—" : $"{apps.Count} aplicativo(s)",
                platforms.Count == 0 ? "Qualquer" : string.Join(", ", platforms),
                builtIn,
                builtIn.Contains("compliantDevice"), builtIn.Contains("approvedApplication"), builtIn.Contains("compliantApplication"), builtIn.Contains("mfa"),
                apps.Contains("All") || apps.Contains("Office365"));
        }
    }

    public async IAsyncEnumerable<SignInAccess> ReadSignInAccessAsync(DateTimeOffset since, int maxPages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var list = new List<SignInAccess>();
        try
        {
            await foreach (var item in ReadSignInAccessCore(since, maxPages, cancellationToken))
            {
                list.Add(item);
            }
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.Forbidden)
        {
            throw new GraphException(ex.Status, GraphErrors.MissingPermission("os sign-ins do Microsoft 365", "AuditLog.Read.All", "Os logs de entrada exigem Microsoft Entra ID P1 ou P2.", "/auditLogs/signIns", ex.Error.WhatHappened));
        }

        foreach (var item in list)
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<SignInAccess> ReadSignInAccessCore(DateTimeOffset since, int maxPages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const string select = "userId,userPrincipalName,appDisplayName,resourceDisplayName,clientAppUsed,createdDateTime,deviceDetail";
        var resources = string.Join(" or ", new[] { "Office 365 Exchange Online", "Office 365 SharePoint Online", "Microsoft Teams Services", "OneDrive SyncEngine", "Microsoft Graph" }.Select(r => $"resourceDisplayName eq '{r}'"));
        var stamp = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var all = $"/auditLogs/signIns?$filter=createdDateTime ge {stamp} and ({resources}) and (signInEventTypes/any(t: t eq 'interactiveUser') or signInEventTypes/any(t: t eq 'nonInteractiveUser'))&$top=500&$select={select}";
        var interactive = $"/auditLogs/signIns?$filter=createdDateTime ge {stamp} and ({resources})&$top=500&$select={select}";

        var summary = new Dictionary<string, Agg>();
        var url = all;
        var pages = 0;
        var triedFallback = false;
        while (url is not null && pages < Math.Max(1, maxPages))
        {
            JsonDocument page;
            try
            {
                page = await client.GetAsync(url, cancellationToken);
            }
            catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest && !triedFallback)
            {
                triedFallback = true; // the tenant does not accept the event-type filter: interactive sign-ins still show who uses what
                url = interactive;
                continue;
            }

            using (page)
            {
                if (page.RootElement.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in items.EnumerateArray())
                    {
                        Add(summary, s);
                    }
                }

                url = page.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }

            pages++;
        }

        foreach (var (key, a) in summary)
        {
            yield return new SignInAccess(key, a.UserId, a.Upn, a.DeviceId, a.DeviceName, a.Os, a.Browser, a.IsManaged, a.IsCompliant, a.TrustType, a.Last,
                string.Join(",", Workloads.Where(a.Workloads.Contains)), a.Count, a.ClientApp);
        }
    }

    private sealed class Agg
    {
        public string? UserId, Upn, DeviceId, DeviceName, Os, Browser, TrustType, ClientApp;
        public bool? IsManaged, IsCompliant;
        public DateTimeOffset Last;
        public int Count;
        public HashSet<string> Workloads = [];
    }

    private static void Add(Dictionary<string, Agg> summary, JsonElement s)
    {
        var when = Date(s, "createdDateTime");
        if (when is null)
        {
            return;
        }

        var detail = s.TryGetProperty("deviceDetail", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        var deviceId = detail.ValueKind == JsonValueKind.Object ? Text(detail, "deviceId") : null;
        var hasDevice = GraphIds.TryNormalize(deviceId, out var normalizedDevice);
        var userId = Text(s, "userId");
        var os = detail.ValueKind == JsonValueKind.Object ? Text(detail, "operatingSystem") : null;
        var browser = detail.ValueKind == JsonValueKind.Object ? Text(detail, "browser") : null;
        var key = hasDevice ? "dev:" + normalizedDevice : $"anon:{userId}|{os}|{browser}".ToLowerInvariant();

        if (!summary.TryGetValue(key, out var a))
        {
            a = summary[key] = new Agg { UserId = userId, Upn = Text(s, "userPrincipalName"), DeviceId = hasDevice ? normalizedDevice : null };
        }

        a.Count++;
        if (when > a.Last)
        {
            a.Last = when.Value;
            a.ClientApp = Text(s, "clientAppUsed");
            a.DeviceName = detail.ValueKind == JsonValueKind.Object ? Text(detail, "displayName") : a.DeviceName;
            a.Os = os;
            a.Browser = browser;
            a.IsManaged = detail.ValueKind == JsonValueKind.Object ? Bool(detail, "isManaged") : null;
            a.IsCompliant = detail.ValueKind == JsonValueKind.Object ? Bool(detail, "isCompliant") : null;
            a.TrustType = detail.ValueKind == JsonValueKind.Object ? Text(detail, "trustType") : null;
        }

        var resource = Text(s, "resourceDisplayName") ?? "";
        if (resource.Contains("Exchange", StringComparison.OrdinalIgnoreCase)) a.Workloads.Add("Exchange");
        else if (resource.Contains("SharePoint", StringComparison.OrdinalIgnoreCase)) a.Workloads.Add("SharePoint");
        else if (resource.Contains("Teams", StringComparison.OrdinalIgnoreCase)) a.Workloads.Add("Teams");
        else if (resource.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)) a.Workloads.Add("OneDrive");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<List<JsonElement>> ReadOptionalAsync(string path, CancellationToken cancellationToken)
    {
        var list = new List<JsonElement>();
        try
        {
            await foreach (var item in client.GetPagedAsync(path, cancellationToken))
            {
                list.Add(item);
            }
        }
        catch (GraphException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // a resource this tenant does not have: nothing to read, not an error
        }

        return list;
    }

    /// <summary>One batched call per child collection (apps, assignments) for the given policy ids.</summary>
    private async Task<Dictionary<string, Dictionary<string, List<JsonElement>>>> ReadChildrenAsync(string basePath, IReadOnlyList<string> ids, string[] children, CancellationToken cancellationToken)
    {
        var result = ids.ToDictionary(i => i, _ => children.ToDictionary(c => c, _ => new List<JsonElement>()));
        var requests = ids.SelectMany((id, i) => children.Select((child, j) => ($"{i}.{j}", $"{basePath}/{id}/{child}"))).ToList();
        var responses = await client.BatchGetAsync(requests, cancellationToken);
        foreach (var response in responses)
        {
            if (response.Status != 200 || response.Body is null)
            {
                continue;
            }

            var parts = response.Id.Split('.');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var i) || !int.TryParse(parts[1], out var j) || i >= ids.Count || j >= children.Length)
            {
                continue;
            }

            if (response.Body.Value.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                result[ids[i]][children[j]].AddRange(items.EnumerateArray().Select(x => x.Clone()));
            }
        }

        return result;
    }

    private async Task<Dictionary<string, string>> ReadGroupNamesAsync(IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var ids = GraphIds.Clean(raw);
        var names = new Dictionary<string, string>();
        if (ids.Count == 0)
        {
            return names;
        }

        try
        {
            foreach (var r in (await client.BatchGetAsync(ids.Select(g => (g, $"/groups/{g}?$select=id,displayName")).ToList(), cancellationToken)).Where(r => r.Status == 200 && r.Body is not null))
            {
                names[r.Id] = Text(r.Body!.Value, "displayName") ?? r.Id;
            }
        }
        catch (GraphException)
        {
            // names are cosmetic: show the ids when the app cannot read groups
        }

        return names;
    }

    private static IEnumerable<string> GroupIdsOf(JsonElement assignment) =>
        assignment.TryGetProperty("target", out var t) && Text(t, "groupId") is { } id ? [id] : [];

    private static IEnumerable<string> GroupIdsOfConfig(JsonElement config) => AssignmentsOf(config).SelectMany(GroupIdsOf);

    private static List<JsonElement> AssignmentsOf(JsonElement config) =>
        config.TryGetProperty("assignments", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(a => a.Clone()).ToList() : [];

    private static (string Summary, bool All, bool Assigned) Summarize(IEnumerable<JsonElement> assignments, IReadOnlyDictionary<string, string> groupNames)
    {
        var parts = new List<string>();
        var all = false;
        foreach (var a in assignments)
        {
            if (!a.TryGetProperty("target", out var t))
            {
                continue;
            }

            var type = Text(t, "@odata.type") ?? "";
            if (type.Contains("allLicensedUsers", StringComparison.OrdinalIgnoreCase)) { all = true; parts.Add("Todos os usuários"); }
            else if (type.Contains("allDevices", StringComparison.OrdinalIgnoreCase)) { all = true; parts.Add("Todos os dispositivos"); }
            else if (type.Contains("exclusion", StringComparison.OrdinalIgnoreCase)) parts.Add("Exclui: " + groupNames.GetValueOrDefault(Text(t, "groupId") ?? "", Text(t, "groupId") ?? "?"));
            else parts.Add("Grupo: " + groupNames.GetValueOrDefault(Text(t, "groupId") ?? "", Text(t, "groupId") ?? "?"));
        }

        return (parts.Count == 0 ? "Sem atribuição" : string.Join("; ", parts.Distinct()), all, parts.Any(p => !p.StartsWith("Exclui", StringComparison.Ordinal)));
    }

    private static Dictionary<string, string> Settings(JsonElement policy, string[] names)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!policy.TryGetProperty(name, out var v))
            {
                continue;
            }

            result[name] = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.Array => string.Join(",", v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
                _ => "",
            };
        }

        return result;
    }

    private static string? AppIdentifier(JsonElement app) =>
        app.TryGetProperty("mobileAppIdentifier", out var id) && id.ValueKind == JsonValueKind.Object ? Text(id, "bundleId") ?? Text(id, "packageId") ?? Text(id, "windowsAppId") : null;

    /// <summary>Managed app configuration has no platform field: the app identifiers say it (Edge is com.microsoft.emmx on Android and com.microsoft.msedge on iOS).</summary>
    public static string PlatformFromApps(IReadOnlyList<string> apps) =>
        apps.Any(a => a.Contains("emmx", StringComparison.OrdinalIgnoreCase)) && !apps.Any(a => a.Contains("msedge", StringComparison.OrdinalIgnoreCase)) ? "Android"
        : apps.Any(a => a.Contains("msedge", StringComparison.OrdinalIgnoreCase)) && !apps.Any(a => a.Contains("emmx", StringComparison.OrdinalIgnoreCase)) ? "iOS"
        : "Não informada";

    private static int? VersionOf(JsonElement p) => int.TryParse(Text(p, "version"), out var v) ? v : null;

    /// <summary>Android Enterprise managed configuration: a JSON object (sometimes base64) of key to value.</summary>
    public static IEnumerable<KeyValuePair<string, string>> ParsePayload(string payload)
    {
        var text = payload.Trim();
        if (!text.StartsWith('{') && !text.StartsWith('['))
        {
            try { text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text)); } catch (FormatException) { yield break; }
        }

        JsonDocument? doc;
        try { doc = JsonDocument.Parse(text); } catch (JsonException) { yield break; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject())
                {
                    yield return new(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (Text(item, "key") is { } key)
                    {
                        yield return new(key, item.TryGetProperty("valueString", out var vs) && vs.ValueKind == JsonValueKind.String ? vs.GetString() ?? "" : item.TryGetProperty("valueBool", out var vb) ? vb.GetRawText() : "");
                    }
                }
            }
        }
    }

    private static List<string> Include(JsonElement conditions, string section, string property) =>
        conditions.ValueKind == JsonValueKind.Object && conditions.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object ? Strings(s, property) : [];

    private static List<string> Strings(JsonElement e, string property) =>
        e.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];

    private static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Text(e, name) is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1 ? d : null;
}
