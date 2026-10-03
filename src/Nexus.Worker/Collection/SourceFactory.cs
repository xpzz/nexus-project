using Nexus.Collectors.ActiveDirectory;
using Nexus.Collectors.Sccm;
using Nexus.Core.Configuration;
using Nexus.Simulation;

namespace Nexus.Worker.Collection;

public interface ISourceFactory
{
    ISccmReader? CreateSccmReader(SccmSettings settings, SccmQueryGate gate);
    IDirectoryReader? CreateDirectoryReader(ActiveDirectorySettings settings);
}

/// <summary>Live sources read the real systems; simulated sources use the synthetic estate.</summary>
public sealed class SourceFactory : ISourceFactory
{
    private readonly Lazy<SyntheticEstate> _estate = new(() => new SyntheticEstate());

    public ISccmReader? CreateSccmReader(SccmSettings settings, SccmQueryGate gate) => settings.Mode switch
    {
        SourceMode.Live => new SqlSccmReader(settings, gate),
        SourceMode.Simulated => _estate.Value.CreateSccmReader(),
        _ => null,
    };

    public IDirectoryReader? CreateDirectoryReader(ActiveDirectorySettings settings) => settings.Mode switch
    {
        SourceMode.Live => new LdapDirectoryReader(settings),
        SourceMode.Simulated => _estate.Value.CreateDirectoryReader(),
        _ => null,
    };
}
