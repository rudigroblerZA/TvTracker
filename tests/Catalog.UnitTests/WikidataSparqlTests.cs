using System.Net;
using Catalog.SyncWorker.Providers;
using Catalog.SyncWorker.Providers.Wikidata;
using Microsoft.Extensions.Time.Testing;

namespace Catalog.UnitTests;

/// <summary>
/// The Wikidata client's two lanes and its 429 cool-off: a person's request goes
/// before background queries already waiting, and fails at once while Wikimedia has asked everyone to wait.
/// </summary>
public class WikidataSparqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Empty = """{"results":{"bindings":[]}}""";
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_waiting_interactive_query_goes_before_waiting_background_ones()
    {
        var first = new TaskCompletionSource();
        var order = new List<string>();
        var handler = new Handler(async query =>
        {
            lock (order) order.Add(query);
            if (query == "A") await first.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Empty) };
        });
        var sparql = new WikidataSparql(new Factory(handler), time);

        var a = Task.Run(() => sparql.QueryAsync("A", Ct));
        await Until(() => order.Count == 1);
        var b = Task.Run(() => sparql.QueryAsync("B", Ct));
        await Task.Delay(50, Ct);
        var c = Task.Run(() =>
        {
            using var lane = ProviderCaller.Begin("search-import", ProviderLane.Interactive);
            return sparql.QueryAsync("C", Ct);
        });
        await Task.Delay(50, Ct);
        first.SetResult();

        // Each query waits a second after the one before, on the fake clock.
        while (!Task.WhenAll(a, b, c).IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, Ct);
        }
        Assert.Equal(["A", "C", "B"], order);
    }

    [Fact]
    public async Task While_wikimedia_asks_to_wait_a_persons_query_fails_at_once()
    {
        var calls = 0;
        var handler = new Handler(_ =>
        {
            Interlocked.Increment(ref calls);
            var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            busy.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return Task.FromResult(busy);
        });
        var sparql = new WikidataSparql(new Factory(handler), time);
        using var lane = ProviderCaller.Begin("history-import", ProviderLane.Interactive);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => sparql.QueryAsync("A", Ct));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => sparql.QueryAsync("B", Ct));
        Assert.Equal(1, calls);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10, Ct);
    }

    private sealed class Handler(Func<string, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            return await answer(WebUtility.UrlDecode(body["query=".Length..]));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
