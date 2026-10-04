using System.Globalization;
using System.Text.Json;

namespace Nexus.Collectors.Netskope;

/// <summary>
/// Maps one client record of the tenant API to <see cref="NetskopeClient"/>, tolerating the field layouts of the API versions
/// (host_info.* nested or flat, last_event.timestamp or last_seen, seconds or milliseconds). Unknown fields are ignored.
/// </summary>
public static class NetskopeParser
{
    public static NetskopeClient? Parse(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var host = item.TryGetProperty("host_info", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;
        var lastEvent = item.TryGetProperty("last_event", out var le) && le.ValueKind == JsonValueKind.Object ? le : default;

        var id = First(item, "_id", "id", "client_id") ?? First(item, "device_id") ?? First(host, "nsdeviceuid");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new NetskopeClient(
            id,
            First(item, "device_id") ?? First(host, "nsdeviceuid"),
            First(host, "hostname", "fqdn") ?? First(item, "hostname", "host_name", "device_name"),
            First(host, "os") ?? First(item, "os", "operating_system"),
            First(host, "os_version") ?? First(item, "os_version"),
            First(host, "serial_number") ?? First(item, "serial_number", "serial"),
            First(host, "device_make") ?? First(item, "device_make", "manufacturer"),
            First(host, "device_model") ?? First(item, "device_model", "model"),
            First(item, "client_version", "agent_version") ?? First(host, "client_version"),
            First(lastEvent, "status") ?? First(item, "agent_status", "status", "client_status"),
            Time(lastEvent, "timestamp") ?? Time(item, "last_seen", "last_event_time", "last_hb_time", "timestamp", "last_updated") ?? Time(host, "last_hb_time"),
            Time(item, "client_install_time", "install_time", "installed_at"),
            First(host, "managementID", "management_id") ?? First(item, "managementID", "management_id"),
            Users(item, lastEvent));
    }

    /// <summary>The records of a page: {"data":[...]}, {"result":[...]}, {"devices":[...]} or a bare array.</summary>
    public static IEnumerable<JsonElement> Records(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        foreach (var name in new[] { "data", "result", "results", "devices", "clients", "items" })
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var list))
            {
                if (list.ValueKind == JsonValueKind.Array)
                {
                    return list.EnumerateArray();
                }

                if (list.ValueKind == JsonValueKind.Object && Records(list) is { } nested && nested.Any())
                {
                    return nested;
                }
            }
        }

        return [];
    }

    public static string? First(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }

        return null;
    }

    /// <summary>Unix seconds, Unix milliseconds or ISO 8601. Zero and the epoch mean "never".</summary>
    public static DateTimeOffset? Time(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number > 0)
            {
                return number > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)number) : DateTimeOffset.FromUnixTimeSeconds((long)number);
            }

            if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) && numeric > 0)
                {
                    return numeric > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)numeric) : DateTimeOffset.FromUnixTimeSeconds((long)numeric);
                }

                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) && parsed.Year > 1970)
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static string? Users(JsonElement item, JsonElement lastEvent)
    {
        if (item.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
        {
            var names = users.EnumerateArray()
                .Select(u => u.ValueKind == JsonValueKind.String ? u.GetString() : First(u, "username", "user", "userkey", "email"))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(5).ToList();
            if (names.Count > 0)
            {
                return string.Join("; ", names);
            }
        }

        return First(item, "user", "username", "user_id") ?? First(lastEvent, "actor");
    }
}
