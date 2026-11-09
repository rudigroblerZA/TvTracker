using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Catalog.SyncWorker.Providers.Wikidata;

/// <summary>
/// Wikidata's query service and Wikipedia's page summaries for the catalog provider, gently: one request at a time and
/// at least a second apart, since Wikimedia limits a shared address hard (CONTEXT.md, Research), and a 429's
/// Retry-After waited out once (up to a minute) before giving up with an <see cref="HttpRequestException"/> for the 429,
/// which every sync loop already treats as "skip this show, try again later", as it does TVmaze's. A 502, 503 or
/// 504, which the query service gives now and then under load, is tried once more 5 s later; nothing else is retried,
/// as with the other Wikimedia clients: the standard resilience handler would retry a 429 until its timeout.
/// </summary>
public sealed class WikidataSparql(IHttpClientFactory httpFactory, TimeProvider time)
{
    public const string HttpClientName = "wikidata";
    private const string Endpoint = "https://query.wikidata.org/sparql";
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GatewayRetryAfter = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset lastAt = DateTimeOffset.MinValue;

    /// <summary>
    /// The result's rows: each a map of variable name to its value as text (an item's full URI, a literal's text).
    /// </summary>
    public async Task<List<Dictionary<string, string>>> QueryAsync(string sparql, CancellationToken ct)
    {
        using var json = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new FormUrlEncodedContent([new("query", sparql)])
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));
            return request;
        }, ct);
        return json is null ? [] : Rows(json.RootElement);
    }

    /// <summary>
    /// The lead of an English Wikipedia article as plain text, by the article's address; null when there's none.
    /// </summary>
    public async Task<string?> WikipediaSummaryAsync(string articleUrl, CancellationToken ct)
    {
        // The address is already percent-encoded ("https://en.wikipedia.org/wiki/Breaking_Bad").
        var title = articleUrl[(articleUrl.LastIndexOf('/') + 1)..];
        using var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"https://en.wikipedia.org/api/rest_v1/page/summary/{title}"), ct);
        return json?.RootElement.TryGetProperty("extract", out var extract) == true ? extract.GetString() : null;
    }

    /// <summary>
    /// Null for a 404.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = lastAt + Spacing - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, ct);
                }
                lastAt = time.GetUtcNow();
                using var request = build();
                using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var after = response.Headers.RetryAfter?.Delta;
                    if (attempt > 0 || after is null || after > MaxWait)
                    {
                        throw new HttpRequestException($"Wikimedia answered 429 Too Many Requests (retry after {after?.TotalSeconds ?? 0:0} s).",
                            null, HttpStatusCode.TooManyRequests);
                    }
                    await Task.Delay(after.Value, time, ct);
                    continue;
                }
                if (attempt == 0 && response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    await Task.Delay(GatewayRetryAfter, time, ct);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                response.EnsureSuccessStatusCode();
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal static List<Dictionary<string, string>> Rows(JsonElement result) =>
        result.GetProperty("results").GetProperty("bindings").EnumerateArray()
            .Select(row => row.EnumerateObject().ToDictionary(v => v.Name, v => v.Value.GetProperty("value").GetString() ?? ""))
            .ToList();

    /// <summary>
    /// A text as a SPARQL string literal, quotes and backslashes escaped and line breaks dropped.
    /// </summary>
    public static string Literal(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
}
