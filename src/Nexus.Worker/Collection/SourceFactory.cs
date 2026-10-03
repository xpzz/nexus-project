using Nexus.Core;
using System.Net.Http;
using Nexus.Collectors.ActiveDirectory;
using Nexus.Collectors.Graph;
using Nexus.Collectors.Sccm;
using Nexus.Core.Configuration;
using Nexus.Core.Security;
using Nexus.Simulation;

namespace Nexus.Worker.Collection;

public interface ISourceFactory
{
    ISccmReader? CreateSccmReader(SccmSettings settings, SccmQueryGate gate);
    IDirectoryReader? CreateDirectoryReader(ActiveDirectorySettings settings);

    /// <summary>
    /// Null when Azure is not configured ("não configurado", never zero). Live uses the collector app with its certificate
    /// (never the service account); demo mode uses the synthetic estate.
    /// </summary>
    IGraphReader? CreateGraphReader(NexusSettings settings);

    /// <summary>Live connection (token + client) for the access check; null when not configured or in demo mode.</summary>
    GraphConnection? CreateGraphConnection(NexusSettings settings);
}

public sealed record GraphConnection(GraphHttpClient Client, ITokenProvider Tokens);

/// <summary>Live sources read the real systems; simulated sources use the synthetic estate.</summary>
public sealed class SourceFactory(NexusPaths paths) : ISourceFactory
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
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

    public IGraphReader? CreateGraphReader(NexusSettings settings)
    {
        if (settings.DemoMode)
        {
            return _estate.Value.CreateGraphReader();
        }

        var connection = CreateGraphConnection(settings);
        return connection is null ? null : new HttpGraphReader(connection.Client);
    }

    public GraphConnection? CreateGraphConnection(NexusSettings settings)
    {
        if (settings.DemoMode)
        {
            return null;
        }

        var azure = AzureSettingsStore.Load(paths);
        if (azure is null)
        {
            return null;
        }

        var certificate = CertificateLookup.Find(azure.Collector.CertificateThumbprint, "coleta do Intune e do Entra ID");
        var tokens = new CertificateTokenProvider(azure.TenantId, azure.Collector.ClientId, certificate, Http);
        return new GraphConnection(new GraphHttpClient(Http, tokens), tokens);
    }
}
