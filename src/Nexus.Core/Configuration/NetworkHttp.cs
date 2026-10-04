using System.Net;

namespace Nexus.Core.Configuration;

public static class NetworkHttp
{
    /// <summary>HttpClient that goes through the configured proxy (with the service identity when asked) or straight out.</summary>
    public static HttpClient Create(NetworkSettings network, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler();
        if (!string.IsNullOrWhiteSpace(network.ProxyUrl))
        {
            handler.Proxy = new WebProxy(network.ProxyUrl.Trim(), BypassOnLocal: true) { UseDefaultCredentials = network.ProxyUseDefaultCredentials };
            handler.UseProxy = true;
        }

        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    public static string Key(NetworkSettings network) => $"{network.ProxyUrl}|{network.ProxyUseDefaultCredentials}";
}
