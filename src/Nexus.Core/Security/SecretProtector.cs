using System.Security.Cryptography;
using System.Text;

namespace Nexus.Core.Security;

public interface ISecretProtector
{
    string Protect(string secret);
    string Unprotect(string protectedSecret);
}

/// <summary>
/// DPAPI with machine scope plus product entropy. The configuration folder ACL restricts the
/// file to Administrators and the service identities, so only they can read the ciphertext.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Azul.Nexus.v1"u8.ToArray();

    public string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotSupported();
        }

        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(data);
    }

    public string Unprotect(string protectedSecret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotSupported();
        }

        var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(data);
    }

    private static PlatformNotSupportedException NotSupported() => new(
        "A proteção de segredos usa DPAPI e só está disponível no Windows. " +
        "Em desenvolvimento, use SQL Server com autenticação integrada ou a variável NEXUS_DB_PASSWORD.");
}
