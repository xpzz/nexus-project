using System.Security.Cryptography;
using System.Text;

namespace Nexus.Core.Setup;

/// <summary>
/// One-time code that unlocks remote access to setup mode before SSO exists (SPEC §4.1).
/// Only the SHA-256 hash is persisted.
/// </summary>
public static class SetupCode
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    // No 0/O/1/I/L to avoid reading mistakes.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        var builder = new StringBuilder(14);
        for (var i = 0; i < 12; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                builder.Append('-');
            }

            builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return builder.ToString();
    }

    public static string Normalize(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));

    public static bool Matches(string candidate, string expectedHash)
    {
        var candidateHash = Encoding.ASCII.GetBytes(Hash(candidate));
        var expected = Encoding.ASCII.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(candidateHash, expected);
    }
}
