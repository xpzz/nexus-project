using System.Runtime.CompilerServices;

namespace Nexus.Collectors.Graph;

/// <summary>In-memory Graph used by tests, simulators and demo mode (no tenant is ever contacted).</summary>
public sealed class FakeGraphReader(IEnumerable<IntuneManagedDevice> managed, IEnumerable<EntraDevice> entra) : IGraphReader
{
    private readonly List<IntuneManagedDevice> _managed = managed.ToList();
    private readonly List<EntraDevice> _entra = entra.ToList();

    public List<IntunePolicy> Policies { get; init; } = [];
    public List<DevicePolicyState> PolicyStates { get; init; } = [];
    public List<MamRegistration> MamRegistrations { get; init; } = [];
    public List<EntraUser> Users { get; init; } = [];
    public Dictionary<string, List<DetectedApp>> DetectedApps { get; init; } = [];

    public async IAsyncEnumerable<IntunePolicy> ReadPoliciesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        foreach (var p in Policies)
        {
            await Task.Yield();
            yield return p;
        }
    }

    public async IAsyncEnumerable<DevicePolicyState> ReadDevicePolicyStatesAsync(IReadOnlyList<string> managedDeviceIds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var wanted = managedDeviceIds.ToHashSet();
        foreach (var s in PolicyStates.Where(s => wanted.Contains(s.ManagedDeviceId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return s;
        }
    }

    public async IAsyncEnumerable<MamRegistration> ReadMamRegistrationsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        foreach (var r in MamRegistrations)
        {
            await Task.Yield();
            yield return r;
        }
    }

    public async IAsyncEnumerable<EntraUser> ReadUsersAsync(IReadOnlyList<string> userIds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var wanted = userIds.ToHashSet();
        foreach (var u in Users.Where(u => wanted.Contains(u.Id)))
        {
            await Task.Yield();
            yield return u;
        }
    }

    public Task<IReadOnlyList<DetectedApp>> ReadDetectedAppsAsync(string managedDeviceId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult<IReadOnlyList<DetectedApp>>(DetectedApps.GetValueOrDefault(managedDeviceId) ?? []);
    }

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
