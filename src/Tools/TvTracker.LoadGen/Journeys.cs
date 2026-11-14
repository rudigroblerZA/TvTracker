using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TvTracker.LoadGen;

/// <param name="Writes">Also follow shows, mark episodes watched and rate, as people do; off, everything is a read.</param>
public sealed record LoadOptions(string BffUrl, int Users, TimeSpan Duration, TimeSpan RampUp, TimeSpan Think, bool Writes, string Password, string Prefix);

/// <summary>
/// One virtual user: signs in, then visits pages until the run ends, each page a burst of the requests the app makes
/// for it, with a think time between pages. Pages are picked by weight, roughly how people use the app: shows most,
/// then home, episodes and search. All through the Mobile BFF, so the gateway is under load too.
/// <para>
/// Only what's switched on is visited: pages and requests that belong to a feature flag are left out when the
/// warm-up finds the feature off (<see cref="ShowPool.Has"/>), so a run measures the app as people have it.
/// </para>
/// </summary>
public sealed class VirtualUser(HttpClient http, Keycloak keycloak, Stats stats, ShowPool pool, LoadOptions options, string username)
{
    private static readonly string[] SearchTerms =
    [
        // Words in titles; then what people mean rather than a title; then typos and missing apostrophes, which fall
        // back to near-spelling matches; then actors, for the people search beside it.
        "the", "love", "crime", "doctor", "house", "star", "dark", "office", "game", "space", "detective", "family", "war",
        "comedy about friends", "slow burn mystery", "space opera", "cosy british crime",
        "breakng bad", "strangr things", "greys anatomy", "severence", "pokemon",
        "pedro pascal", "bryan cranston", "olivia colman"
    ];

    private static readonly string[] DiscoverLists = ["hidden-gems", "most-consistent", "rollercoasters", "best-finales", "bingeable", "fan-favorites"];

    private readonly Session session = new(keycloak, username, options.Password, options.BffUrl);

    public async Task RunAsync(CancellationToken ct)
    {
        var pages = new List<(int Weight, Func<CancellationToken, Task> Visit)>
        {
            (4, ShowPageAsync), (3, HomeAsync), (2, EpisodePageAsync), (2, SearchAsync), (1, DiscoverAsync), (1, StatsAsync)
        };
        // The pages of features that can be switched off, when they're on.
        foreach (var (feature, weight, visit) in new (string, int, Func<CancellationToken, Task>)[]
        {
            (Features.Feed, 1, FeedAsync), (Features.Tonight, 1, TonightAsync), (Features.WaitingRoom, 1, WaitingRoomAsync),
            (Features.Universes, 1, UniversesAsync)
        })
        {
            if (pool.Has(feature))
            {
                pages.Add((weight, visit));
            }
        }
        if (options.Writes)
        {
            pages.Add((2, TrackAsync));
        }
        var totalWeight = pages.Sum(p => p.Weight);

        while (!ct.IsCancellationRequested)
        {
            var roll = Random.Shared.Next(totalWeight);
            var page = pages.First(p => (roll -= p.Weight) < 0);
            try
            {
                await page.Visit(ct);
                // Think time: ±50%, so users don't move in lockstep.
                await Task.Delay(TimeSpan.FromMilliseconds(options.Think.TotalMilliseconds * (0.5 + Random.Shared.NextDouble())), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private Task HomeAsync(CancellationToken ct) => Task.WhenAll(
        // Every page asks for the plan (it decides what the menu and the pages offer) and the app's name and colour.
        GetAsync("identity", "/me/plan", "/identity/api/v1/me/plan", ct),
        GetAsync("identity", "/branding", "/identity/api/v1/branding", ct),
        GetAsync("catalog", "/shows/trending", "/catalog/api/v1/shows/trending", ct),
        GetAsync("tracking", "/up-next", "/tracking/api/v1/up-next", ct),
        GetAsync("recommendations", "/for-you", "/recommendations/api/v1/for-you", ct),
        GetAsync("notifications", "/inbox", "/notifications/api/v1/inbox", ct));

    private async Task ShowPageAsync(CancellationToken ct)
    {
        var show = pool.Show();
        var requests = new List<Task>
        {
            GetAsync("catalog", "/shows/{id}", $"/catalog/api/v1/shows/{show}", ct),
            GetAsync("ratings", "/shows/{id}/ratings/summary", $"/ratings/api/v1/shows/{show}/ratings/summary", ct),
            GetAsync("tracking", "/follows/{id}", $"/tracking/api/v1/follows/{show}", ct),
            GetAsync("analytics", "/shows/{id}/insights", $"/analytics/api/v1/shows/{show}/insights", ct),
            GetAsync("recommendations", "/shows/{id}/similar", $"/recommendations/api/v1/shows/{show}/similar", ct)
        };
        if (pool.Has(Features.Universes))
        {
            // As the show page does. 404 for a show in no universe, which is most of them: the report's 404 column.
            requests.Add(GetAsync("catalog", "/shows/{id}/universe", $"/catalog/api/v1/shows/{show}/universe", ct));
        }
        if (pool.Has(Features.FriendsOnShows))
        {
            requests.Add(GetAsync("social", "/shows/{id}/friends", $"/social/api/v1/shows/{show}/friends", ct));
        }
        if (pool.Has(Features.CommunityInsights))
        {
            requests.Add(GetAsync("ratings", "/shows/{id}/community-insights", $"/ratings/api/v1/shows/{show}/community-insights", ct));
        }
        await Task.WhenAll(requests);
        if (Random.Shared.Next(3) == 0)
        {
            await GetAsync("catalog", "/shows/{id}/seasons/{n}", $"/catalog/api/v1/shows/{show}/seasons/1", ct);
        }
    }

    private async Task EpisodePageAsync(CancellationToken ct)
    {
        var (show, episode) = await pool.EpisodeAsync(this, ct);
        if (episode is null)
        {
            return;
        }
        await Task.WhenAll(
            GetAsync("catalog", "/episodes/{id}", $"/catalog/api/v1/episodes/{episode}", ct),
            GetAsync("ratings", "/episodes/{id}/comments", $"/ratings/api/v1/episodes/{episode}/comments?reveal=true", ct),
            GetAsync("ratings", "/episodes/{id}/reactions", $"/ratings/api/v1/episodes/{episode}/reactions", ct),
            GetAsync("ratings", "/episodes/{id}/ratings/summary", $"/ratings/api/v1/episodes/{episode}/ratings/summary", ct),
            GetAsync("catalog", "/shows/{id}", $"/catalog/api/v1/shows/{show}", ct));
    }

    /// <summary>
    /// A search as the search page makes it: suggestions while the term is typed, then the search itself (titles,
    /// near-spelled titles, and by meaning) with the people search beside it.
    /// </summary>
    private async Task SearchAsync(CancellationToken ct)
    {
        var term = SearchTerms[Random.Shared.Next(SearchTerms.Length)];
        foreach (var typed in new[] { term[..Math.Min(3, term.Length)], term[..Math.Min(7, term.Length)] }.Distinct())
        {
            await GetAsync("catalog", "/shows/suggest", $"/catalog/api/v1/shows/suggest?q={Uri.EscapeDataString(typed)}", ct);
        }
        // The first search for a term makes the catalog ask its provider for matches it doesn't have (at most once
        // in six hours a term, and it can wait ten seconds on the provider), so that one is timed apart: it measures
        // the provider, where the rest measure the catalog.
        var search = pool.FirstSearch(term) ? "/shows?search= (a term's first: may import)" : "/shows?search=";
        await Task.WhenAll(
            GetAsync("catalog", search, $"/catalog/api/v1/shows?search={Uri.EscapeDataString(term)}&pageSize=24", ct),
            GetAsync("catalog", "/people/search", $"/catalog/api/v1/people/search?q={Uri.EscapeDataString(term)}", ct));
    }

    private Task DiscoverAsync(CancellationToken ct) => Task.WhenAll(
        GetAsync("analytics", "/insights/discover/{list}", $"/analytics/api/v1/insights/discover/{DiscoverLists[Random.Shared.Next(DiscoverLists.Length)]}", ct),
        GetAsync("analytics", "/insights/premieres", "/analytics/api/v1/insights/premieres", ct));

    private Task FeedAsync(CancellationToken ct) => GetAsync("social", "/feed", "/social/api/v1/feed", ct);

    private Task TonightAsync(CancellationToken ct) => GetAsync("tracking", "/tonight/queue", "/tracking/api/v1/tonight/queue?perShow=2", ct);

    private Task WaitingRoomAsync(CancellationToken ct) => GetAsync("analytics", "/me/waiting", "/analytics/api/v1/me/waiting", ct);

    private Task UniversesAsync(CancellationToken ct) => GetAsync("catalog", "/universes", "/catalog/api/v1/universes", ct);

    private Task StatsAsync(CancellationToken ct) => Task.WhenAll(
        GetAsync("tracking", "/stats", "/tracking/api/v1/stats", ct),
        GetAsync("analytics", "/me/taste", "/analytics/api/v1/me/taste", ct),
        GetAsync("tracking", "/calendar", "/tracking/api/v1/calendar", ct));

    /// <summary>
    /// Follows a show, marks its first episode watched and rates it; now and then unfollows one again, so a long run
    /// doesn't follow the whole catalog.
    /// </summary>
    private async Task TrackAsync(CancellationToken ct)
    {
        var (show, episode) = await pool.EpisodeAsync(this, ct);
        if (Random.Shared.Next(4) == 0)
        {
            await SendAsync(HttpMethod.Delete, "tracking", "/follows/{id}", $"/tracking/api/v1/follows/{show}", null, ct);
            return;
        }
        await SendAsync(HttpMethod.Post, "tracking", "/follows/{id}", $"/tracking/api/v1/follows/{show}", new { status = 0 }, ct);
        if (episode is not null)
        {
            await SendAsync(HttpMethod.Post, "tracking", "/watched", "/tracking/api/v1/watched",
                new { episodeIds = new[] { episode }, watchedAtUtc = DateTimeOffset.UtcNow }, ct);
        }
        await SendAsync(HttpMethod.Put, "ratings", "/shows/{id}/rating", $"/ratings/api/v1/shows/{show}/rating", new { stars = Random.Shared.Next(3, 6) }, ct);
    }

    public Task<HttpResponseMessage?> GetForPoolAsync(string service, string name, string path, CancellationToken ct) =>
        SendRawAsync(HttpMethod.Get, service, name, path, null, ct);

    /// <summary>
    /// The status a GET answers, or null when it couldn't be reached. Not timed or counted: it's the tool asking a
    /// question of its own, not something a user does.
    /// </summary>
    public async Task<System.Net.HttpStatusCode?> ProbeAsync(string path, CancellationToken ct)
    {
        try
        {
            using var request = await session.RequestAsync(HttpMethod.Get, path, null, ct);
            using var response = await http.SendAsync(request, ct);
            return response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task GetAsync(string service, string name, string path, CancellationToken ct) =>
        (await SendRawAsync(HttpMethod.Get, service, name, path, null, ct))?.Dispose();

    private async Task SendAsync(HttpMethod method, string service, string name, string path, object? body, CancellationToken ct) =>
        (await SendRawAsync(method, service, name, path, body, ct))?.Dispose();

    private async Task<HttpResponseMessage?> SendRawAsync(HttpMethod method, string service, string name, string path, object? body, CancellationToken ct)
    {
        using var request = await session.RequestAsync(method, path, body, ct);
        var label = $"{method.Method} {service} {name}";
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await http.SendAsync(request, ct);
            // Read the body, as a client would; the time includes it.
            await response.Content.LoadIntoBufferAsync(ct);
            stats.Record(label, watch.Elapsed.TotalMilliseconds, (int)response.StatusCode);
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            stats.Record(label, watch.Elapsed.TotalMilliseconds, 0);
            return null;
        }
    }
}

/// <summary>
/// The parts of the app behind a feature flag that a run can visit, each with the request that shows whether it's on:
/// a flag that's off answers 404 from its endpoint. Only features whose endpoints the services gate are here;
/// Discover, premieres and your taste have no flag, and are always visited.
/// </summary>
public static class Features
{
    public const string Feed = "the feed";
    public const string Tonight = "tonight";
    public const string WaitingRoom = "the waiting room";
    public const string Universes = "universes";
    public const string FriendsOnShows = "friends on show pages";
    public const string CommunityInsights = "community insights";

    /// <summary>
    /// The probe for each; <c>{show}</c> is filled with a show from the pool.
    /// </summary>
    public static readonly (string Feature, string Probe)[] Probes =
    [
        (Feed, "/social/api/v1/feed"),
        (Tonight, "/tracking/api/v1/tonight/queue?perShow=1"),
        (WaitingRoom, "/analytics/api/v1/me/waiting"),
        (Universes, "/catalog/api/v1/universes"),
        (FriendsOnShows, "/social/api/v1/shows/{show}/friends"),
        (CommunityInsights, "/ratings/api/v1/shows/{show}/community-insights")
    ];
}

/// <summary>
/// The shows (and their first season's episodes) users visit: the catalog's trending list and a few genres, fetched
/// once at start, episodes as they're first needed. The warm-up also finds which flagged features are on.
/// </summary>
public sealed class ShowPool
{
    private readonly List<Guid> shows = [];
    private readonly HashSet<string> features = [];

    /// <summary>
    /// Whether the warm-up found the feature switched on.
    /// </summary>
    public bool Has(string feature) => features.Contains(feature);

    /// <summary>
    /// The features the warm-up found off, so the run can say what it leaves out.
    /// </summary>
    public IReadOnlyList<string> Off => Features.Probes.Select(p => p.Feature).Where(f => !features.Contains(f)).ToList();

    private readonly Dictionary<Guid, Task<List<Guid>>> episodes = [];

    public int Count => shows.Count;

    public async Task FillAsync(VirtualUser user, CancellationToken ct)
    {
        // Lists only: a search here would have the catalog ask its provider for new shows before the run starts.
        foreach (var path in new[] { "/catalog/api/v1/shows/trending", "/catalog/api/v1/shows?genre=Drama", "/catalog/api/v1/shows?genre=Comedy", "/catalog/api/v1/shows?genre=Crime" })
        {
            using var response = await user.GetForPoolAsync("catalog", "(warm-up)", path, ct);
            if (response is { IsSuccessStatusCode: true })
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var items = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement : json.RootElement.GetProperty("items");
                shows.AddRange(items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            }
        }
        shows.Sort();
        var distinct = shows.Distinct().ToList();
        shows.Clear();
        shows.AddRange(distinct);
        if (shows.Count == 0)
        {
            return;
        }

        // Which flagged features are on. Only a 404 says "off": a service that's slow to wake (no answer, or the
        // gateway's 502, 503 or 504 for it) is asked again after a pause, and one that still can't be reached counts
        // as on, so its feature is visited and shows in the report as errors rather than being quietly left out.
        foreach (var (feature, probe) in Features.Probes)
        {
            System.Net.HttpStatusCode? status = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                status = await user.ProbeAsync(probe.Replace("{show}", shows[0].ToString()), ct);
                if (status is not (null or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable
                    or System.Net.HttpStatusCode.GatewayTimeout) || attempt == 2)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            if (status != System.Net.HttpStatusCode.NotFound)
            {
                features.Add(feature);
            }
        }
    }

    private readonly HashSet<string> searched = [];

    /// <summary>
    /// Whether this is the run's first search for the term (the one that may wait on the catalog's provider).
    /// </summary>
    public bool FirstSearch(string term)
    {
        lock (searched)
        {
            return searched.Add(term);
        }
    }

    public Guid Show() => shows[Random.Shared.Next(shows.Count)];

    /// <summary>
    /// A random show and one of its first season's episodes (null when it has none).
    /// </summary>
    public async Task<(Guid Show, Guid? Episode)> EpisodeAsync(VirtualUser user, CancellationToken ct)
    {
        var show = Show();
        Task<List<Guid>> load;
        lock (episodes)
        {
            if (!episodes.TryGetValue(show, out load!))
            {
                episodes[show] = load = LoadEpisodesAsync(user, show, ct);
            }
        }
        var list = await load;
        return (show, list.Count == 0 ? null : list[Random.Shared.Next(list.Count)]);
    }

    private static async Task<List<Guid>> LoadEpisodesAsync(VirtualUser user, Guid show, CancellationToken ct)
    {
        using var response = await user.GetForPoolAsync("catalog", "/shows/{id}/seasons/{n}", $"/catalog/api/v1/shows/{show}/seasons/1", ct);
        if (response is not { IsSuccessStatusCode: true })
        {
            return [];
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("episodes").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }
}
