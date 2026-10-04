using System.Runtime.CompilerServices;

namespace Nexus.Collectors.Xdr;

/// <summary>In-memory XDR table used by tests, simulators and demo mode (no database is contacted).</summary>
public sealed class FakeXdrReader(IEnumerable<XdrEndpoint> endpoints) : IXdrReader
{
    private readonly List<XdrEndpoint> _endpoints = endpoints.ToList();

    public bool FailWithUnreachable { get; set; }

    public async IAsyncEnumerable<XdrEndpoint> ReadEndpointsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (FailWithUnreachable)
        {
            throw new InvalidOperationException("Servidor SQL do XDR indisponível (simulado).");
        }

        foreach (var e in _endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return e;
        }
    }
}
