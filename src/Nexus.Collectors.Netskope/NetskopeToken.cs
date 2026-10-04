using Nexus.Core.Configuration;
using Nexus.Core.Security;

namespace Nexus.Collectors.Netskope;

public static class NetskopeToken
{
    public const string EnvironmentVariable = "NEXUS_NETSKOPE_TOKEN";

    /// <summary>The API token: the development variable wins, otherwise the DPAPI-protected value in the configuration. Null when none is set.</summary>
    public static string? Resolve(NetskopeSettings settings, ISecretProtector? protector = null)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        return settings.ProtectedToken is { Length: > 0 } protectedToken ? (protector ?? new DpapiSecretProtector()).Unprotect(protectedToken) : null;
    }
}
