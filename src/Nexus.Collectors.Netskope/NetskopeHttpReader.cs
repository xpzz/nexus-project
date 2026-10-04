using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nexus.Core.Configuration;
using Nexus.Core.Errors;

namespace Nexus.Collectors.Netskope;

public sealed class NetskopeException(HttpStatusCode status, NexusError error) : Exception(error.ToString())
{
    public HttpStatusCode Status { get; } = status;

    public NexusError Error { get; } = error;
}

/// <summary>
/// Reads the client data with the tenant API token (never logged, never part of an error message), paging by limit and offset,
/// honoring 429 Retry-After and retrying 5xx. Read-only: the only verb is GET.
/// </summary>
public sealed class NetskopeHttpReader(HttpClient http, NetskopeSettings settings, string token, Func<TimeSpan, CancellationToken, Task>? delay = null) : INetskopeReader
{
    private const int MaxAttempts = 5;
    private const int MaxPages = 10_000;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>The request URL without the token (safe to show and to log).</summary>
    public static string BuildUrl(NetskopeSettings settings, int offset, bool withToken, string token = "")
    {
        var path = settings.ClientsPath.StartsWith('/') ? settings.ClientsPath : "/" + settings.ClientsPath;
        var query = new List<string> { $"limit={settings.PageSize}", $"{settings.OffsetParameter}={offset}" };
        if (withToken && settings.TokenPlacement.Equals("query", StringComparison.OrdinalIgnoreCase))
        {
            query.Insert(0, "token=" + Uri.EscapeDataString(token));
        }

        return $"https://{settings.Tenant.Trim().TrimEnd('/')}{path}?{string.Join("&", query)}";
    }

    public async IAsyncEnumerable<NetskopeClient> ReadClientsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>();
        for (var page = 0; page < MaxPages; page++)
        {
            var offset = page * settings.PageSize;
            using var doc = await GetAsync(offset, cancellationToken);
            var records = NetskopeParser.Records(doc.RootElement).ToList();
            var fresh = 0;
            foreach (var record in records)
            {
                if (NetskopeParser.Parse(record) is { } client && seen.Add(client.Id))
                {
                    fresh++;
                    yield return client;
                }
            }

            // A short page ends the list; a page that adds nothing new stops an API that ignores the offset.
            if (records.Count < settings.PageSize || fresh == 0)
            {
                yield break;
            }
        }
    }

    /// <summary>One raw page, for the probe command (shape discovery without printing values).</summary>
    public Task<JsonDocument> ReadRawPageAsync(CancellationToken cancellationToken) => GetAsync(0, cancellationToken);

    private async Task<JsonDocument> GetAsync(int offset, CancellationToken cancellationToken)
    {
        var url = BuildUrl(settings, offset, withToken: true, token);
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!settings.TokenPlacement.Equals("query", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation("Netskope-Api-Token", token);
                }

                request.Headers.Accept.ParseAdd("application/json");
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _ = ex; // the message may carry the URL with the token: never surfaced
                await _delay(Backoff(attempt), cancellationToken);
                continue;
            }
            catch (HttpRequestException)
            {
                throw new NetskopeException(HttpStatusCode.ServiceUnavailable, Errors.Unreachable(settings.Tenant));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                await _delay(Backoff(attempt), cancellationToken);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NetskopeException(HttpStatusCode.RequestTimeout, Errors.Unreachable(settings.Tenant));
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    var doc = JsonDocument.Parse(body);
                    // v1 reports failures with 200 and {"status":"error", ...}
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("status", out var status)
                        && status.ValueKind == JsonValueKind.String && status.GetString() is "error" or "failure" or "fail")
                    {
                        var message = NetskopeParser.First(doc.RootElement, "errorCode", "message", "msg", "error") ?? "erro sem detalhe";
                        doc.Dispose();
                        throw new NetskopeException(HttpStatusCode.BadRequest, Errors.ApiError(message));
                    }

                    return doc;
                }

                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError;
                if (transient && attempt < MaxAttempts)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                    await _delay(wait > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : wait, cancellationToken);
                    continue;
                }

                throw new NetskopeException(response.StatusCode, Errors.FromStatus(response.StatusCode, settings.ClientsPath));
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));

    public static class Errors
    {
        public static NexusError FromStatus(HttpStatusCode status, string path) => status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new NexusError("NEXUS-NS-401", "O Netskope recusou o token da API.",
                "Os clientes Netskope não entram no inventário e a atividade deles não conta.",
                "Gere um novo token no console do Netskope (Settings › Tools › REST API) com acesso ao endpoint de clientes e rode: nexusctl netskope-configure --tenant <tenant> (informe o token quando pedido)."),
            HttpStatusCode.NotFound => new NexusError("NEXUS-NS-404", $"O endereço {path} não existe neste tenant.",
                "Os clientes Netskope não entram no inventário.",
                "Confirme o caminho na documentação 'Get Client Data' do seu tenant e ajuste com: nexusctl netskope-configure --path <caminho>. Use 'nexusctl netskope-test' para ver a resposta."),
            HttpStatusCode.TooManyRequests => new NexusError("NEXUS-NS-429", "O Netskope limitou as chamadas da API.", "A coleta fica para o próximo ciclo; o último resultado válido é mantido.",
                "Aumente o intervalo da coleta ou espere; o limite se renova em minutos."),
            _ => new NexusError($"NEXUS-NS-{(int)status}", $"O Netskope respondeu {(int)status} em {path}.", "Os clientes Netskope não entram no inventário nesta coleta.",
                "Rode 'nexusctl netskope-test' e confira o tenant, o caminho e o token."),
        };

        public static NexusError Unreachable(string tenant) => new("NEXUS-NS-001", $"Não foi possível alcançar {tenant}.", "Os clientes Netskope não entram no inventário.",
            "Confira o nome do tenant e, se o servidor só sai à internet pelo proxy, configure-o: nexusctl network-proxy --url http://proxy:8080.");

        public static NexusError ApiError(string message) => new("NEXUS-NS-002", $"O Netskope devolveu um erro: {message}.", "Os clientes Netskope não entram no inventário nesta coleta.",
            "Confira o token, o caminho e os parâmetros com 'nexusctl netskope-test'.");
    }
}
