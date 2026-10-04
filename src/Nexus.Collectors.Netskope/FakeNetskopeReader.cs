using System.Runtime.CompilerServices;

namespace Nexus.Collectors.Netskope;

/// <summary>In-memory Netskope clients used by tests, simulators and demo mode (the tenant is never contacted).</summary>
public sealed class FakeNetskopeReader(IEnumerable<NetskopeClient> clients) : INetskopeReader
{
    private readonly List<NetskopeClient> _clients = clients.ToList();

    public bool FailWithUnauthorized { get; set; }

    public async IAsyncEnumerable<NetskopeClient> ReadClientsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (FailWithUnauthorized)
        {
            throw new NetskopeException(System.Net.HttpStatusCode.Unauthorized, NetskopeHttpReader.Errors.FromStatus(System.Net.HttpStatusCode.Unauthorized, "/api/v1/clients"));
        }

        foreach (var c in _clients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return c;
        }
    }
}
