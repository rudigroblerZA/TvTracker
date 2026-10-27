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
/// </summary>
public sealed class VirtualUser(HttpClient http, Keycloak keycloak, Stats stats, ShowPool pool, LoadOptions options, string username)
{
    private static readonly string[] SearchTerms =
    [
        "the", "love", "crime", "doctor", "house", "star", "dark", "office", "game", "space", "detective", "family", "war",
        "comedy about friends", "slow burn mystery", "space opera", "cosy british crime"
    ];

    private static readonly string[] DiscoverLists = ["hidden-gems", "most-consistent", "rollercoasters", "best-finales", "bingeable", "fan-favorites"];

    private string? token;
    private DateTimeOffset refreshAt;

    public async Task RunAsync(CancellationToken ct)
    {
        var pages = new (int Weight, Func<CancellationToken, Task> Visit)[]
        {
            (4, ShowPageAsync), (3, HomeAsync), (2, EpisodePageAsync), (2, SearchAsync), (1, DiscoverAsync), (1, FeedAsync), (1, StatsAsync)
        };
        if (options.Writes)
        {
            pages = [.. pages, (2, TrackAsync)];
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
        GetAsync("catalog", "/shows/trending", "/catalog/api/v1/shows/trending", ct),
        GetAsync("tracking", "/up-next", "/tracking/api/v1/up-next", ct),
        GetAsync("recommendations", "/for-you", "/recommendations/api/v1/for-you", ct),
        GetAsync("notifications", "/inbox", "/notifications/api/v1/inbox", ct));

    private async Task ShowPageAsync(CancellationToken ct)
    {
        var show = pool.Show();
        await Task.WhenAll(
            GetAsync("catalog", "/shows/{id}", $"/catalog/api/v1/shows/{show}", ct),
            GetAsync("ratings", "/shows/{id}/ratings/summary", $"/ratings/api/v1/shows/{show}/ratings/summary", ct),
            GetAsync("tracking", "/follows/{id}", $"/tracking/api/v1/follows/{show}", ct),
            GetAsync("analytics", "/shows/{id}/insights", $"/analytics/api/v1/shows/{show}/insights", ct),
            GetAsync("recommendations", "/shows/{id}/similar", $"/recommendations/api/v1/shows/{show}/similar", ct),
            GetAsync("social", "/shows/{id}/friends", $"/social/api/v1/shows/{show}/friends", ct),
            GetAsync("ratings", "/shows/{id}/community-insights", $"/ratings/api/v1/shows/{show}/community-insights", ct));
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

    private Task SearchAsync(CancellationToken ct)
    {
        var term = SearchTerms[Random.Shared.Next(SearchTerms.Length)];
        return GetAsync("catalog", "/shows?q=", $"/catalog/api/v1/shows?q={Uri.EscapeDataString(term)}", ct);
    }

    private Task DiscoverAsync(CancellationToken ct) => Task.WhenAll(
        GetAsync("analytics", "/insights/discover/{list}", $"/analytics/api/v1/insights/discover/{DiscoverLists[Random.Shared.Next(DiscoverLists.Length)]}", ct),
        GetAsync("analytics", "/insights/premieres", "/analytics/api/v1/insights/premieres", ct));

    private Task FeedAsync(CancellationToken ct) => GetAsync("social", "/feed", "/social/api/v1/feed", ct);

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

    private async Task GetAsync(string service, string name, string path, CancellationToken ct) =>
        (await SendRawAsync(HttpMethod.Get, service, name, path, null, ct))?.Dispose();

    private async Task SendAsync(HttpMethod method, string service, string name, string path, object? body, CancellationToken ct) =>
        (await SendRawAsync(method, service, name, path, body, ct))?.Dispose();

    private async Task<HttpResponseMessage?> SendRawAsync(HttpMethod method, string service, string name, string path, object? body, CancellationToken ct)
    {
        if (token is null || DateTimeOffset.UtcNow >= refreshAt)
        {
            (token, refreshAt) = await keycloak.SignInAsync(username, options.Password, ct);
        }

        using var request = new HttpRequestMessage(method, options.BffUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
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
/// The shows (and their first season's episodes) users visit: the catalog's trending list and a few genres, fetched
/// once at start, episodes as they're first needed.
/// </summary>
public sealed class ShowPool
{
    private readonly List<Guid> shows = [];
    private readonly Dictionary<Guid, Task<List<Guid>>> episodes = [];

    public int Count => shows.Count;

    public async Task FillAsync(VirtualUser user, CancellationToken ct)
    {
        foreach (var path in new[] { "/catalog/api/v1/shows/trending", "/catalog/api/v1/shows?q=the", "/catalog/api/v1/shows?genre=Drama", "/catalog/api/v1/shows?genre=Comedy" })
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
    }

    public Guid Show() => shows[Random.Shared.Next(shows.Count)];

    /// <summary>A random show and one of its first season's episodes (null when it has none).</summary>
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
