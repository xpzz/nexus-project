using System.Runtime.CompilerServices;

namespace Nexus.Collectors.ActiveDirectory;

/// <summary>In-memory AD used by tests, simulators and demo mode.</summary>
public sealed class FakeDirectoryReader(IEnumerable<AdComputer> computers) : IDirectoryReader
{
    private readonly List<AdComputer> _computers = computers.ToList();

    public bool FailWithUnreachable { get; set; }

    public async IAsyncEnumerable<AdComputer> ReadComputersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (FailWithUnreachable)
        {
            throw new System.DirectoryServices.Protocols.LdapException(81, "Servidor LDAP indisponível (simulado).");
        }

        foreach (var computer in _computers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return computer;
        }
    }
}
