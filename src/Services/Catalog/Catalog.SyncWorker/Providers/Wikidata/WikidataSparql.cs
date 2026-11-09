using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Catalog.SyncWorker.Providers.Wikidata;

/// <summary>
/// Wikidata's query service and Wikipedia's page summaries for the catalog provider, gently: one request at a time and
/// at least a second apart, interactive callers first, since Wikimedia limits a shared address hard (CONTEXT.md,
/// Research). A 429's Retry-After holds every caller off until it passes: background work waits it out once (up to a
/// minute), and anything else gets an <see cref="HttpRequestException"/> for the 429, which every sync loop already
/// treats as "skip this show, try again later", as it does TVmaze's. A 502, 503 or
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

    private readonly (Queue<TaskCompletionSource> Interactive, Queue<TaskCompletionSource> Background) waiters = (new(), new());
    private readonly Lock sync = new();
    private bool busy;
    private DateTimeOffset lastAt = DateTimeOffset.MinValue;
    private DateTimeOffset busyUntil = DateTimeOffset.MinValue;

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
        // Everything after "/wiki/": a title can hold a slash of its own ("20/20 (American TV program)").
        var at = articleUrl.IndexOf("/wiki/", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        var title = Uri.EscapeDataString(Uri.UnescapeDataString(articleUrl[(at + "/wiki/".Length)..]));
        using var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"https://en.wikipedia.org/api/rest_v1/page/summary/{title}"), ct);
        return json?.RootElement.TryGetProperty("extract", out var extract) == true ? extract.GetString() : null;
    }

    /// <summary>
    /// Null for a 404.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var interactive = TvMazeCaller.Current.Lane == TvMazeLane.Interactive;
        for (var attempt = 0; ; attempt++)
        {
            // Wikimedia asked everyone to wait: a person's request fails at once (search answers from the catalog, a
            // history import says "try again later"); background work waits it out, without holding the slot.
            if (busyUntil - time.GetUtcNow() is { Ticks: > 0 } cooling)
            {
                if (interactive || cooling > MaxWait)
                {
                    throw Busy(cooling);
                }
                await Task.Delay(cooling, time, ct);
            }

            HttpStatusCode status;
            TimeSpan? retryAfter;
            await AcquireAsync(interactive, ct);
            try
            {
                var wait = lastAt + Spacing - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, ct);
                }
                lastAt = time.GetUtcNow();
                using var request = build();
                using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
                status = response.StatusCode;
                retryAfter = response.Headers.RetryAfter?.Delta;
                if (status == HttpStatusCode.NotFound)
                {
                    return null;
                }
                if (response.IsSuccessStatusCode)
                {
                    return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                }
            }
            finally
            {
                Release();
            }

            if (status == HttpStatusCode.TooManyRequests)
            {
                var after = retryAfter ?? MaxWait;
                busyUntil = time.GetUtcNow() + after;
                if (attempt > 0 || interactive || after > MaxWait)
                {
                    throw Busy(after);
                }
                continue;
            }
            if (attempt == 0 && status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                await Task.Delay(GatewayRetryAfter, time, ct);
                continue;
            }
            throw new HttpRequestException($"Wikimedia answered {(int)status}.", null, status);
        }
    }

    private static HttpRequestException Busy(TimeSpan after) =>
        new($"Wikimedia answered 429 Too Many Requests (retry after {after.TotalSeconds:0} s).", null, HttpStatusCode.TooManyRequests);

    /// <summary>
    /// One request at a time, a waiting interactive caller (search and history imports, by <see cref="TvMazeCaller"/>'s
    /// lane) always before a background one, as TVmaze's limiter does: a cast-credits round shouldn't put a search
    /// behind dozens of queries.
    /// </summary>
    private Task AcquireAsync(bool interactive, CancellationToken ct)
    {
        lock (sync)
        {
            // Free means no live waiter: Release hands the slot straight to one, and frees it only with both queues empty.
            if (!busy)
            {
                busy = true;
                return Task.CompletedTask;
            }
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (interactive ? waiters.Interactive : waiters.Background).Enqueue(waiter);
            if (ct.CanBeCanceled)
            {
                // A cancelled waiter stays queued; Release skips it, since it can't be completed again. The registration
                // goes once the wait ends, so a loop's long-lived token doesn't collect one per query.
                var registration = ct.Register(() => waiter.TrySetCanceled(ct));
                waiter.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }
            return waiter.Task;
        }
    }

    private void Release()
    {
        lock (sync)
        {
            foreach (var queue in new[] { waiters.Interactive, waiters.Background })
            {
                while (queue.TryDequeue(out var next))
                {
                    if (next.TrySetResult())
                    {
                        return;
                    }
                }
            }
            busy = false;
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
