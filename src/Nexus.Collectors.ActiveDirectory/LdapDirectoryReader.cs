using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Nexus.Core.Configuration;

namespace Nexus.Collectors.ActiveDirectory;

/// <summary>
/// Paged LDAP reader with Kerberos/Negotiate signing and sealing (or LDAPS), using the identity
/// of the Worker service. Reads only the attributes of <see cref="AdAttributes.Computer"/>.
/// </summary>
public sealed class LdapDirectoryReader(ActiveDirectorySettings settings) : IDirectoryReader
{
    public async IAsyncEnumerable<AdComputer> ReadComputersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var server = string.IsNullOrWhiteSpace(settings.Server) ? settings.Domain : settings.Server;
        var port = settings.UseLdaps ? 636 : 389;
        using var connection = new LdapConnection(new LdapDirectoryIdentifier(server, port))
        {
            AuthType = AuthType.Negotiate,
            Credential = CredentialCache.DefaultNetworkCredentials,
            Timeout = TimeSpan.FromMinutes(2),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (settings.UseLdaps)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }
        else
        {
            connection.SessionOptions.Signing = true;
            connection.SessionOptions.Sealing = true;
        }

        connection.Bind();

        var bases = settings.SearchBases.Count > 0 ? settings.SearchBases : [DomainToDn(settings.Domain)];
        foreach (var searchBase in bases)
        {
            byte[]? cookie = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new SearchRequest(searchBase, "(objectCategory=computer)", SearchScope.Subtree, AdAttributes.Computer);
                var paging = new PageResultRequestControl(settings.PageSize) { Cookie = cookie ?? [] };
                request.Controls.Add(paging);

                var response = (SearchResponse)await Task.Factory.FromAsync(
                    (callback, state) => connection.BeginSendRequest(request, PartialResultProcessing.NoPartialResultSupport, callback, state),
                    connection.EndSendRequest,
                    null);

                foreach (SearchResultEntry entry in response.Entries)
                {
                    yield return Map(entry);
                }

                cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            }
            while (cookie is { Length: > 0 });
        }
    }

    public static string DomainToDn(string domain) =>
        string.Join(",", domain.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part => $"DC={part}"));

    private static AdComputer Map(SearchResultEntry entry)
    {
        var uac = int.TryParse(Single(entry, "userAccountControl"), out var flags) ? flags : 0;
        var guidBytes = entry.Attributes["objectGUID"]?.GetValues(typeof(byte[])).Cast<byte[]>().FirstOrDefault();
        return new AdComputer(
            guidBytes is { Length: 16 } ? new Guid(guidBytes) : Guid.Empty,
            Single(entry, "name") ?? "",
            Single(entry, "dNSHostName"),
            Single(entry, "operatingSystem"),
            Single(entry, "operatingSystemVersion"),
            FromFileTime(Single(entry, "lastLogonTimestamp")),
            FromFileTime(Single(entry, "pwdLastSet")),
            FromGeneralizedTime(Single(entry, "whenCreated")),
            (uac & AdAttributes.AccountDisable) == 0,
            entry.DistinguishedName);
    }

    private static string? Single(SearchResultEntry entry, string attribute) =>
        entry.Attributes[attribute]?.GetValues(typeof(string)).Cast<string>().FirstOrDefault();

    public static DateTimeOffset? FromFileTime(string? value) =>
        long.TryParse(value, out var fileTime) && fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc()
            ? DateTimeOffset.FromFileTime(fileTime).ToUniversalTime()
            : null;

    public static DateTimeOffset? FromGeneralizedTime(string? value) =>
        DateTimeOffset.TryParseExact(value, "yyyyMMddHHmmss.0'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
