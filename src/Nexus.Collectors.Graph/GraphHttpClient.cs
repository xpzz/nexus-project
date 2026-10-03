using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nexus.Collectors.Graph;

/// <summary>
/// GET-only Microsoft Graph client: paging by @odata.nextLink, 429 with Retry-After, backoff on 5xx and
/// timeouts, one token refresh after a 401. Read-only by construction (there is no write method).
/// </summary>
public sealed class GraphHttpClient(HttpClient http, ITokenProvider tokens, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0";
    private const int MaxAttempts = 6;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async IAsyncEnumerable<JsonElement> GetPagedAsync(string relativeUrl, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? url = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? relativeUrl : BaseUrl + relativeUrl;
        while (url is not null)
        {
            using var page = await GetAsync(url, cancellationToken);
            var root = page.RootElement;
            if (root.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    yield return item.Clone();
                }
            }

            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
    }

    public async Task<JsonDocument> GetAsync(string relativeOrAbsoluteUrl, CancellationToken cancellationToken)
    {
        var url = relativeOrAbsoluteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? relativeOrAbsoluteUrl : BaseUrl + relativeOrAbsoluteUrl;
        var refreshed = false;
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await _delay(Backoff(attempt), cancellationToken);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                await _delay(Backoff(attempt), cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
                {
                    refreshed = true;
                    tokens.Invalidate();
                    continue;
                }

                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError;
                if (transient && attempt < MaxAttempts)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                    await _delay(wait > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : wait, cancellationToken);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new GraphException(response.StatusCode, GraphErrors.FromGraph(response.StatusCode, body, new Uri(url).AbsolutePath));
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
}
