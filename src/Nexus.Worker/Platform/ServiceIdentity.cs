using System.Security.Principal;

namespace Nexus.Worker.Platform;

public static class ServiceIdentity
{
    /// <summary>Name of the identity this process runs as, recorded with every access check.</summary>
    public static string Current() =>
        OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>
    /// Account that must receive SQL grants. A virtual account reaches a remote SQL as the
    /// computer account (DOMAIN\SERVER$), which is why remote SQL requires a gMSA (SPEC §5.2).
    /// </summary>
    public static string SqlLoginFor(string sqlServer)
    {
        var current = Current();
        if (!current.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase) || IsLocal(sqlServer))
        {
            return current;
        }

        return $"{Environment.UserDomainName}\\{Environment.MachineName}$";
    }

    public static bool IsLocal(string sqlServer)
    {
        var host = sqlServer.Split('\\', ',')[0].Trim();
        return host is "." or "(local)" or "localhost" or "127.0.0.1"
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || host.StartsWith(Environment.MachineName + ".", StringComparison.OrdinalIgnoreCase);
    }
}
