using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nexus.Collectors.Graph;

/// <summary>
/// Read-only Microsoft Graph client: paging by @odata.nextLink, 429 with Retry-After, backoff on 5xx and
/// timeouts, one token refresh after a 401. Read-only by construction: the only POST is the $batch envelope,
/// and it is built here from GET sub-requests only.
/// </summary>
public sealed record BatchResponse(string Id, int Status, JsonElement? Body);

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

    public Task<JsonDocument> GetAsync(string relativeOrAbsoluteUrl, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, relativeOrAbsoluteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? relativeOrAbsoluteUrl : BaseUrl + relativeOrAbsoluteUrl, null, cancellationToken);

    public const int BatchSize = 20;

    /// <summary>Requests dropped by <see cref="Sanitize"/> since this client was created (blank id or URL, duplicate id, absolute URL, empty path segment).</summary>
    public int DroppedRequests { get; private set; }

    /// <summary>
    /// A single malformed entry makes Graph reject the whole batch with 400, which would fail a collection of thousands of records.
    /// Entries with a blank id or URL, a repeated id, an absolute URL or an empty path segment (for example <c>/users//</c>, from a null id) are dropped.
    /// </summary>
    private IReadOnlyList<(string Id, string Url)> Sanitize(IReadOnlyList<(string Id, string Url)> requests)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<(string, string)>(requests.Count);
        foreach (var (id, url) in requests)
        {
            var valid = !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
                && !url.Split('?')[0].Contains("//", StringComparison.Ordinal) && seen.Add(id);
            if (valid)
            {
                clean.Add((id, url));
            }
            else
            {
                DroppedRequests++;
            }
        }

        return clean;
    }

    /// <summary>
    /// Runs GET requests through the Graph $batch endpoint, 20 at a time. Throttled or unavailable sub-requests
    /// (429, 503, 504) are retried on their own with the Retry-After they report; other statuses are returned as they are.
    /// </summary>
    public async Task<IReadOnlyList<BatchResponse>> BatchGetAsync(IReadOnlyList<(string Id, string Url)> requests, CancellationToken cancellationToken)
    {
        requests = Sanitize(requests);
        var results = new List<BatchResponse>(requests.Count);
        foreach (var chunk in requests.Chunk(BatchSize))
        {
            var pending = chunk.ToList();
            for (var round = 1; pending.Count > 0; round++)
            {
                var body = JsonSerializer.Serialize(new { requests = pending.Select(r => new { id = r.Id, method = "GET", url = r.Url }) });
                using var doc = await SendAsync(HttpMethod.Post, BaseUrl + "/$batch", body, cancellationToken);
                var retry = new List<(string Id, string Url)>();
                TimeSpan wait = TimeSpan.Zero;
                foreach (var item in doc.RootElement.GetProperty("responses").EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString() ?? "";
                    var status = item.GetProperty("status").GetInt32();
                    if (status is 429 or 503 or 504 && round < 6)
                    {
                        retry.Add(pending.First(p => p.Id == id));
                        if (item.TryGetProperty("headers", out var h) && h.TryGetProperty("Retry-After", out var ra) && int.TryParse(ra.GetString(), out var seconds))
                        {
                            wait = TimeSpan.FromSeconds(Math.Max(wait.TotalSeconds, Math.Min(seconds, 120)));
                        }

                        continue;
                    }

                    results.Add(new BatchResponse(id, status, item.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object ? b.Clone() : null));
                }

                pending = retry;
                if (pending.Count > 0)
                {
                    await _delay(wait > TimeSpan.Zero ? wait : Backoff(round), cancellationToken);
                }
            }
        }

        return results;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string? json, CancellationToken cancellationToken)
    {
        var refreshed = false;
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (json is not null)
                {
                    request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                }

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
