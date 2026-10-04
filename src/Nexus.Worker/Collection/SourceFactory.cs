using Nexus.Core;
using System.Net.Http;
using Nexus.Collectors.ActiveDirectory;
using Nexus.Collectors.Graph;
using Nexus.Collectors.Netskope;
using Nexus.Collectors.Sccm;
using Nexus.Collectors.Xdr;
using Nexus.Core.Configuration;
using Nexus.Core.Security;
using Nexus.Simulation;

namespace Nexus.Worker.Collection;

public interface ISourceFactory
{
    ISccmReader? CreateSccmReader(SccmSettings settings, SccmQueryGate gate);
    IDirectoryReader? CreateDirectoryReader(ActiveDirectorySettings settings);
    IXdrReader? CreateXdrReader(XdrSettings settings);

    /// <summary>Null when Netskope is not configured or has no token. Goes out through the configured proxy.</summary>
    INetskopeReader? CreateNetskopeReader(NexusSettings settings);

    /// <summary>
    /// Null when Azure is not configured ("não configurado", never zero). Live uses the collector app with its certificate
    /// (never the service account); demo mode uses the synthetic estate.
    /// </summary>
    IGraphReader? CreateGraphReader(NexusSettings settings);

    /// <summary>App protection, app configuration, Conditional Access and sign-ins. Null when Azure is not configured.</summary>
    IGraphGovernanceReader? CreateGovernanceReader(NexusSettings settings);

    /// <summary>Live connection (token + client) for the access check; null when not configured or in demo mode.</summary>
    GraphConnection? CreateGraphConnection(NexusSettings settings);
}

public sealed record GraphConnection(GraphHttpClient Client, ITokenProvider Tokens);

/// <summary>Live sources read the real systems; simulated sources use the synthetic estate.</summary>
public sealed class SourceFactory(NexusPaths paths) : ISourceFactory
{
    private readonly Dictionary<string, HttpClient> _clients = [];
    private readonly Lock _clientsLock = new();

    /// <summary>One long-lived HttpClient per proxy setting (a changed proxy in the configuration gets a new client).</summary>
    private HttpClient HttpFor(NetworkSettings network)
    {
        lock (_clientsLock)
        {
            var key = NetworkHttp.Key(network);
            if (!_clients.TryGetValue(key, out var client))
            {
                _clients[key] = client = NetworkHttp.Create(network, TimeSpan.FromMinutes(5));
            }

            return client;
        }
    }
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

    public IXdrReader? CreateXdrReader(XdrSettings settings) => settings.Mode switch
    {
        SourceMode.Live => new SqlXdrReader(settings),
        SourceMode.Simulated => _estate.Value.CreateXdrReader(),
        _ => null,
    };

    public INetskopeReader? CreateNetskopeReader(NexusSettings settings)
    {
        switch (settings.Netskope.Mode)
        {
            case SourceMode.Simulated:
                return _estate.Value.CreateNetskopeReader();
            case SourceMode.Live:
                var token = NetskopeToken.Resolve(settings.Netskope);
                return token is null || string.IsNullOrWhiteSpace(settings.Netskope.Tenant) ? null : new NetskopeHttpReader(HttpFor(settings.Network), settings.Netskope, token);
            default:
                return null;
        }
    }

    public IGraphReader? CreateGraphReader(NexusSettings settings)
    {
        if (settings.DemoMode)
        {
            return _estate.Value.CreateGraphReader();
        }

        var connection = CreateGraphConnection(settings);
        return connection is null ? null : new HttpGraphReader(connection.Client);
    }

    public IGraphGovernanceReader? CreateGovernanceReader(NexusSettings settings)
    {
        if (settings.DemoMode)
        {
            return _estate.Value.CreateGovernanceReader();
        }

        var connection = CreateGraphConnection(settings);
        return connection is null ? null : new HttpGovernanceReader(connection.Client);
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
        var http = HttpFor(settings.Network);
        var tokens = new CertificateTokenProvider(azure.TenantId, azure.Collector.ClientId, certificate, http);
        return new GraphConnection(new GraphHttpClient(http, tokens), tokens);
    }
}
