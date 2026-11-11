using System.Net;
using System.Text;
using Catalog.SyncWorker.Providers.Tvdb;
using Microsoft.Extensions.Options;

namespace Catalog.UnitTests;

/// <summary>
/// The sign-in around TheTVDB's API: a token from the key, reused, renewed once when refused; and the provider's
/// reading of paged answers.
/// </summary>
public class TvdbClientTests
{
    /// <summary>
    /// TheTVDB, as far as these tests go: answers from a function, and a record of what was asked.
    /// </summary>
    private sealed class FakeTvdb(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://tvdb.test/v4/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {request.Headers.Authorization?.Parameter}".TrimEnd());
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (TvdbClient Client, IOptions<TvdbOptions> Options) Client(FakeTvdb tvdb, string? pin = null)
    {
        var options = Options.Create(new TvdbOptions { ApiKey = "key-1", Pin = pin, FeaturedCountries = ["usa", "gbr"] });
        return (new TvdbClient(tvdb, options), options);
    }

    [Fact]
    public async Task It_signs_in_with_the_key_once_and_sends_the_token()
    {
        string? loginBody = null;
        var tvdb = new FakeTvdb((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login"))
            {
                loginBody = body;
                return Json("""{ "status": "success", "data": { "token": "t1" } }""");
            }
            return Json("""{ "status": "success", "data": { "id": 81189, "name": "Breaking Bad" } }""");
        });
        var (client, _) = Client(tvdb, pin: "PIN9");

        var first = await client.GetAsync<TvdbSeries>("series/81189", TestContext.Current.CancellationToken);
        await client.GetAsync<TvdbSeries>("series/81189", TestContext.Current.CancellationToken);

        Assert.Equal("Breaking Bad", first!.Data!.Name);
        Assert.Equal(["POST /v4/login", "GET /v4/series/81189 t1", "GET /v4/series/81189 t1"], tvdb.Requests);
        Assert.Contains("\"apikey\":\"key-1\"", loginBody);
        Assert.Contains("\"pin\":\"PIN9\"", loginBody);
    }

    [Fact]
    public async Task A_refused_token_is_renewed_once_and_the_request_sent_again()
    {
        var tokens = 0;
        var tvdb = new FakeTvdb((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/login") ? Json($$"""{ "data": { "token": "t{{++tokens}}" } }""")
            : request.Headers.Authorization!.Parameter == "t1" ? Json("""{ "status": "failure" }""", HttpStatusCode.Unauthorized)
            : Json("""{ "data": { "id": 1, "name": "Show" } }"""));
        var (client, _) = Client(tvdb);

        var found = await client.GetAsync<TvdbSeries>("series/1", TestContext.Current.CancellationToken);

        Assert.Equal("Show", found!.Data!.Name);
        Assert.Equal(["POST /v4/login", "GET /v4/series/1 t1", "POST /v4/login", "GET /v4/series/1 t2"], tvdb.Requests);
    }

    [Fact]
    public async Task A_token_refused_twice_is_an_error_not_a_loop()
    {
        var tvdb = new FakeTvdb((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/login") ? Json("""{ "data": { "token": "t" } }""")
            : Json("{}", HttpStatusCode.Unauthorized));
        var (client, _) = Client(tvdb);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync<TvdbSeries>("series/1", TestContext.Current.CancellationToken));
        Assert.Equal(4, tvdb.Requests.Count);
    }

    [Fact]
    public async Task A_record_TheTVDB_doesnt_have_is_null()
    {
        var tvdb = new FakeTvdb((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/login") ? Json("""{ "data": { "token": "t" } }""")
            : Json("""{ "status": "failure", "message": "Not Found" }""", HttpStatusCode.NotFound));
        var (client, options) = Client(tvdb);
        var provider = new TvdbCatalogProvider(client, options, TimeProvider.System);

        Assert.Null(await provider.GetShowAsync("424242", TestContext.Current.CancellationToken));
        Assert.Null(await provider.LookUpShowByTvdbAsync("424242", TestContext.Current.CancellationToken));
        // Not an id at all: not asked for.
        Assert.Null(await provider.GetShowAsync("Q1079", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(tvdb.Requests, r => r.Contains("Q1079"));
    }

    [Fact]
    public async Task A_shows_episodes_are_read_page_by_page()
    {
        var tvdb = new FakeTvdb((request, _) => request.RequestUri!.PathAndQuery switch
        {
            "/v4/login" => Json("""{ "data": { "token": "t" } }"""),
            "/v4/series/7/extended" => Json("""{ "data": { "id": 7, "name": "Long Show", "status": { "name": "Continuing" }, "firstAired": "2020-01-01" } }"""),
            "/v4/series/7/episodes/default?page=0" => Json("""
                { "data": { "episodes": [ { "id": 1, "name": "One", "aired": "2020-01-01", "number": 1, "seasonNumber": 1 } ] },
                  "links": { "next": "https://tvdb.test/v4/series/7/episodes/default?page=1" } }
                """),
            "/v4/series/7/episodes/default?page=1" => Json("""
                { "data": { "episodes": [ { "id": 2, "name": "Two", "aired": "2020-01-08", "number": 2, "seasonNumber": 1 } ] },
                  "links": { "next": null } }
                """),
            var other => throw new InvalidOperationException($"Not expected: {other}")
        });
        var (client, options) = Client(tvdb);

        var show = await new TvdbCatalogProvider(client, options, TimeProvider.System).GetShowAsync("7", TestContext.Current.CancellationToken);

        Assert.Equal(["One", "Two"], show!.Episodes.Select(e => e.Title));
    }

    [Fact]
    public async Task Changed_shows_are_the_updated_series_and_the_series_of_updated_episodes()
    {
        var tvdb = new FakeTvdb((request, _) =>
        {
            var query = request.RequestUri!.PathAndQuery;
            return query == "/v4/login" ? Json("""{ "data": { "token": "t" } }""")
                : query.Contains("type=series") ? Json("""
                    { "data": [ { "recordType": "series", "recordId": 10, "timeStamp": 1700000000, "method": "update" } ], "links": { "next": null } }
                    """)
                : query.Contains("type=episodes") ? Json("""
                    { "data": [
                        { "recordType": "episodes", "recordId": 501, "seriesId": 10, "timeStamp": 1700000900 },
                        { "recordType": "episodes", "recordId": 502, "seriesId": 20, "timeStamp": 1700000100 } ],
                      "links": { "next": null } }
                    """)
                : throw new InvalidOperationException($"Not expected: {query}");
        });
        var (client, options) = Client(tvdb);

        var changed = await new TvdbCatalogProvider(client, options, TimeProvider.System)
            .GetChangedShowsAsync(TimeSpan.FromHours(2), TestContext.Current.CancellationToken);

        Assert.Equal(["10", "20"], changed!.Keys.Order());
        // The later of the show's own change and its episode's.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000900), changed["10"]);
    }

    [Fact]
    public async Task The_most_popular_are_the_best_scored_across_the_configured_countries()
    {
        var tvdb = new FakeTvdb((request, _) =>
        {
            var query = request.RequestUri!.PathAndQuery;
            return query == "/v4/login" ? Json("""{ "data": { "token": "t" } }""")
                : query.Contains("country=usa") ? Json("""{ "data": [ { "id": 1, "score": 900 }, { "id": 2, "score": 100 } ] }""")
                : query.Contains("country=gbr") ? Json("""{ "data": [ { "id": 3, "score": 500 }, { "id": 1, "score": 900 } ] }""")
                : throw new InvalidOperationException($"Not expected: {query}");
        });
        var (client, options) = Client(tvdb);

        var ids = await new TvdbCatalogProvider(client, options, TimeProvider.System)
            .GetPopularShowIdsAsync(2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["1", "3"], ids);
        Assert.All(tvdb.Requests.Skip(1), r => Assert.Contains("lang=eng&sort=score&sortType=desc", r));
    }
}
