using System.Runtime.CompilerServices;

namespace Nexus.Collectors.Graph;

/// <summary>In-memory Graph used by tests, simulators and demo mode (no tenant is ever contacted).</summary>
public sealed class FakeGraphReader(IEnumerable<IntuneManagedDevice> managed, IEnumerable<EntraDevice> entra) : IGraphReader
{
    private readonly List<IntuneManagedDevice> _managed = managed.ToList();
    private readonly List<EntraDevice> _entra = entra.ToList();

    public bool FailWithForbidden { get; set; }

    public async IAsyncEnumerable<IntuneManagedDevice> ReadManagedDevicesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        foreach (var device in _managed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return device;
        }
    }

    public async IAsyncEnumerable<EntraDevice> ReadEntraDevicesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        foreach (var device in _entra)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return device;
        }
    }

    private void ThrowIfFailing()
    {
        if (FailWithForbidden)
        {
            throw new GraphException(System.Net.HttpStatusCode.Forbidden,
                GraphErrors.FromGraph(System.Net.HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges (simulado)"}}""", "/deviceManagement/managedDevices"));
        }
    }
}
