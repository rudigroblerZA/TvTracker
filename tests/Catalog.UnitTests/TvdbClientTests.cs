using System.Net;
using System.Text;
using Catalog.SyncWorker.Providers.Tvdb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

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

    private static (TvdbClient Client, IOptions<TvdbOptions> Options) Client(FakeTvdb tvdb, string? pin = null, TimeProvider? time = null)
    {
        var options = Options.Create(new TvdbOptions { ApiKey = "key-1", Pin = pin });
        return (new TvdbClient(tvdb, options, time ?? TimeProvider.System), options);
    }

    private static TvdbCatalogProvider Provider(TvdbClient client, IOptions<TvdbOptions> options) =>
        new(client, options, TimeProvider.System, NullLogger<TvdbCatalogProvider>.Instance);

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
        var provider = Provider(client, options);

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
            "/v4/series/7/extended?meta=translations" => Json("""{ "data": { "id": 7, "name": "Long Show", "originalLanguage": "eng", "status": { "name": "Continuing" }, "firstAired": "2020-01-01" } }"""),
            "/v4/series/7/episodes/official?page=0" => Json("""
                { "data": { "episodes": [ { "id": 1, "name": "One", "aired": "2020-01-01", "number": 1, "seasonNumber": 1 } ] },
                  "links": { "next": "https://tvdb.test/v4/series/7/episodes/official?page=1" } }
                """),
            "/v4/series/7/episodes/official?page=1" => Json("""
                { "data": { "episodes": [ { "id": 2, "name": "Two", "aired": "2020-01-08", "number": 2, "seasonNumber": 1 } ] },
                  "links": { "next": null } }
                """),
            var other => throw new InvalidOperationException($"Not expected: {other}")
        });
        var (client, options) = Client(tvdb);

        var show = await Provider(client, options).GetShowAsync("7", TestContext.Current.CancellationToken);

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

        var changed = await Provider(client, options)
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

        var ids = await Provider(client, options)
            .GetPopularShowIdsAsync(2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["1", "3"], ids);
        Assert.All(tvdb.Requests.Skip(1), r => Assert.Contains("lang=eng&sort=score&sortType=desc", r));
    }

    [Fact]
    public async Task A_refused_key_isnt_tried_again_for_a_while()
    {
        var time = new FakeTimeProvider();
        var tvdb = new FakeTvdb((_, _) => Json("""{ "status": "failure", "message": "InvalidAPIKey: apikey invalid" }""", HttpStatusCode.Unauthorized));
        var (client, _) = Client(tvdb, time: time);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync<TvdbSeries>("series/1", TestContext.Current.CancellationToken));
        }
        Assert.Equal(["POST /v4/login"], tvdb.Requests);

        time.Advance(TvdbClient.RefusedFor);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync<TvdbSeries>("series/1", TestContext.Current.CancellationToken));
        Assert.Equal(2, tvdb.Requests.Count);
    }

    [Fact]
    public async Task An_answer_that_cant_be_read_is_a_failed_request()
    {
        var tvdb = new FakeTvdb((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/login") ? Json("""{ "data": { "token": "t" } }""")
            : Json("""{ "data": { "id": "not a number" } }"""));
        var (client, _) = Client(tvdb);

        // The synchronizer skips a show on HttpRequestException; a JsonException would end its whole round.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync<TvdbSeries>("series/1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_show_in_another_language_is_read_in_ours_where_TheTVDB_has_it()
    {
        var tvdb = new FakeTvdb((request, _) => request.RequestUri!.PathAndQuery switch
        {
            "/v4/login" => Json("""{ "data": { "token": "t" } }"""),
            "/v4/series/9/extended?meta=translations" => Json("""
                { "data": { "id": 9, "name": "오징어 게임", "overview": "한국어 줄거리", "originalLanguage": "kor", "originalCountry": "kor",
                    "firstAired": "2021-09-17", "status": { "name": "Ended" },
                    "translations": {
                      "nameTranslations": [ { "language": "kor", "name": "오징어 게임" }, { "language": "eng", "name": "Squid Game" } ],
                      "overviewTranslations": [ { "language": "eng", "overview": "Players risk their lives." } ] } } }
                """),
            "/v4/series/9/episodes/official?page=0" => Json("""
                { "data": { "episodes": [
                    { "id": 1, "name": "무궁화 꽃이 피던 날", "aired": "2021-09-17", "number": 1, "seasonNumber": 1 },
                    { "id": 2, "name": "지옥", "aired": "2021-09-17", "number": 2, "seasonNumber": 1 } ] }, "links": { "next": null } }
                """),
            // The translated listing carries its episodes inside the series; the second has no English name.
            "/v4/series/9/episodes/official/eng?page=0" => Json("""
                { "data": { "series": { "id": 9, "episodes": [
                    { "id": 1, "name": "Red Light, Green Light", "overview": "The game begins.", "number": 1, "seasonNumber": 1 },
                    { "id": 2, "name": null, "number": 2, "seasonNumber": 1 } ] } }, "links": { "next": null } }
                """),
            var other => throw new InvalidOperationException($"Not expected: {other}")
        });
        var (client, options) = Client(tvdb);

        var show = await Provider(client, options).GetShowAsync("9", TestContext.Current.CancellationToken);

        Assert.Equal("Squid Game", show!.Title);
        Assert.Equal("Players risk their lives.", show.Overview);
        Assert.Equal(["Red Light, Green Light", "지옥"], show.Episodes.Select(e => e.Title));
        Assert.Equal("The game begins.", show.Episodes[0].Overview);
    }

    [Fact]
    public async Task A_translated_listing_with_nothing_in_aired_order_falls_back_on_its_own()
    {
        var tvdb = new FakeTvdb((request, _) => request.RequestUri!.PathAndQuery switch
        {
            "/v4/login" => Json("""{ "data": { "token": "t" } }"""),
            "/v4/series/8/extended?meta=translations" => Json("""{ "data": { "id": 8, "name": "Dark", "originalLanguage": "deu" } }"""),
            "/v4/series/8/episodes/official?page=0" => Json("""{ "data": { "episodes": [ { "id": 1, "name": "Geheimnisse", "number": 1, "seasonNumber": 1 } ] } }"""),
            // TheTVDB has no aired-order listing in English for it (404), but has a default-order one.
            "/v4/series/8/episodes/official/eng?page=0" => Json("""{ "status": "failure" }""", HttpStatusCode.NotFound),
            "/v4/series/8/episodes/default/eng?page=0" => Json("""{ "data": { "series": { "id": 8, "episodes": [ { "id": 1, "name": "Secrets" } ] } } }"""),
            var other => throw new InvalidOperationException($"Not expected: {other}")
        });
        var (client, options) = Client(tvdb);

        var show = await Provider(client, options).GetShowAsync("8", TestContext.Current.CancellationToken);

        Assert.Equal(["Secrets"], show!.Episodes.Select(e => e.Title));
    }

    [Fact]
    public async Task A_series_with_nothing_in_aired_order_is_read_in_its_default_order()
    {
        var tvdb = new FakeTvdb((request, _) => request.RequestUri!.PathAndQuery switch
        {
            "/v4/login" => Json("""{ "data": { "token": "t" } }"""),
            "/v4/series/5/extended?meta=translations" => Json("""{ "data": { "id": 5, "name": "Odd", "originalLanguage": "eng" } }"""),
            "/v4/series/5/episodes/official?page=0" => Json("""{ "data": { "episodes": [] } }"""),
            "/v4/series/5/episodes/default?page=0" => Json("""{ "data": { "episodes": [ { "id": 1, "name": "Only", "number": 1, "seasonNumber": 1 } ] } }"""),
            var other => throw new InvalidOperationException($"Not expected: {other}")
        });
        var (client, options) = Client(tvdb);

        var show = await Provider(client, options).GetShowAsync("5", TestContext.Current.CancellationToken);

        Assert.Equal(["Only"], show!.Episodes.Select(e => e.Title));
    }

    [Fact]
    public async Task Created_records_count_as_changes_and_deleted_ones_dont()
    {
        var tvdb = new FakeTvdb((request, _) =>
        {
            var query = request.RequestUri!.PathAndQuery;
            Assert.DoesNotContain("action=", query);
            return query == "/v4/login" ? Json("""{ "data": { "token": "t" } }""")
                : query.Contains("type=series") ? Json("""{ "data": [ { "recordId": 30, "timeStamp": 1700000000, "methodInt": 3 } ] }""")
                : Json("""
                    { "data": [ { "recordId": 601, "seriesId": 10, "timeStamp": 1700000500, "methodInt": 1 },
                                { "recordId": 602, "seriesId": 40, "timeStamp": 1700000600, "methodInt": 3 } ] }
                    """);
        });
        var (client, options) = Client(tvdb);

        var changed = await Provider(client, options).GetChangedShowsAsync(TimeSpan.FromHours(2), TestContext.Current.CancellationToken);

        // A new episode of show 10; show 30 and show 40's episode were deleted.
        Assert.Equal(["10"], changed!.Keys);
    }

    [Fact]
    public async Task A_feed_longer_than_the_page_limit_gives_what_was_read()
    {
        var pages = 0;
        var tvdb = new FakeTvdb((request, _) =>
        {
            var query = request.RequestUri!.PathAndQuery;
            return query == "/v4/login" ? Json("""{ "data": { "token": "t" } }""")
                : query.Contains("type=people") ? Json($$"""{ "data": [ { "recordId": {{++pages}}, "timeStamp": 1700000000 } ], "links": { "next": "more" } }""")
                : throw new InvalidOperationException($"Not expected: {query}");
        });
        var (client, options) = Client(tvdb);

        var changed = await Provider(client, options).GetChangedPeopleAsync(TimeSpan.FromDays(7), TestContext.Current.CancellationToken);

        Assert.Equal(100, changed!.Count);
    }

    [Fact]
    public void Featured_countries_default_to_the_US_and_UK_and_a_setting_replaces_them()
    {
        Assert.Equal(["usa", "gbr"], new TvdbOptions().Countries);
        Assert.Equal(["jpn"], new TvdbOptions { FeaturedCountries = ["jpn"] }.Countries);

        // As configuration binds it: the setting alone, not added to the defaults.
        var bound = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tvdb:FeaturedCountries:0"] = "jpn" })
            .Build().GetSection("Tvdb").Get<TvdbOptions>()!;
        Assert.Equal(["jpn"], bound.Countries);
    }
}
