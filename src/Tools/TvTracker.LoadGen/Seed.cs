using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TvTracker.LoadGen;

/// <param name="Months">How far back the oldest watches go.</param>
/// <param name="Seed">The random seed: the same seed and catalog give each user the same name, persona, zone and shows;
/// watch dates are counted back from when it runs, so they, and what follows from them, differ from day to day.</param>
public sealed record SeedOptions(string BffUrl, int Users, string Password, string Prefix, int Months, int Seed);

/// <summary>
/// Demo data: users who look like people, so insights, the social feed and the figures behind the privacy rule (5+
/// users or ratings) have something to show. Everything goes through the app's own endpoints, as the users themselves,
/// so every service's copy fills from events exactly as in use.
/// <list type="bullet">
/// <item><b>Taste:</b> each user has a persona (genres they lean to) and follows 8 to 20 shows, most in those genres.</item>
/// <item><b>History, dated:</b> watches go through Bring your history's import (<c>/watched/import</c>), so each keeps
/// its own time: evenings in the user's time zone, an episode every few days, now and then a binge, starting up to
/// <see cref="SeedOptions.Months"/> back. Only episodes aired by then.</item>
/// <item><b>How it ends:</b> finished, still watching, stopped part way or planned, with the status set to match.</item>
/// <item><b>Ratings that agree:</b> stars come from a quality each show has for everyone (a hash of its id), the
/// persona's liking for its genres, and a little noise, lower when the user stopped. Random 3-5 stars made taste twins
/// mechanical; these agree the way people do.</item>
/// <item><b>A few posts and follows:</b> a short comment on some watched episodes (through moderation, so no links), and
/// each user follows a few of the others, earlier runs' included.</item>
/// <item><b>Once:</b> the display name is set last, as the mark of a finished user; a user who has it is skipped, so
/// seeding again adds only new users, and one a failed run left half done is finished (imports, follows and ratings
/// are idempotent; a comment may be posted again).</item>
/// </list>
/// </summary>
public sealed class Seeder(HttpClient http, Keycloak keycloak, SeedOptions options, Func<int, string> username)
{
    private sealed record Persona(string Name, string[] Genres);

    private static readonly Persona[] Personas =
    [
        new("crime", ["Crime", "Thriller", "Mystery", "Drama"]),
        new("comedy", ["Comedy", "Family", "Romance"]),
        new("genre", ["Science-Fiction", "Fantasy", "Supernatural", "Horror", "Adventure"]),
        new("prestige", ["Drama", "History", "Legal", "War"]),
        new("animation", ["Anime", "Comedy", "Adventure", "Fantasy"]),
        new("easy", ["Food", "Travel", "Nature", "Comedy"])   // reality is a TVmaze type, not a genre
    ];

    private static readonly string[] FirstNames =
    [
        "Ava", "Ben", "Chloe", "Dev", "Ella", "Femi", "Grace", "Hugo", "Isla", "Jon", "Kemi", "Leo", "Maya", "Niko", "Olu",
        "Priya", "Quinn", "Rosa", "Sam", "Tara", "Umar", "Vera", "Wes", "Ximena", "Yusuf", "Zoe"
    ];

    private static readonly string[] Zones = ["Europe/London", "America/New_York", "America/Los_Angeles", "Australia/Sydney", "Africa/Johannesburg", "Europe/Berlin"];

    private static readonly string[] Comments =
    [
        "Didn't see that ending coming.", "Slow start, but this one finally clicked for me.", "The cast is on another level here.",
        "That last scene stayed with me.", "Bit of a filler episode, but fun.", "Best episode of the season so far.",
        "The music in this one!", "I need the next one now.", "Not sure about that choice, honestly.", "Rewatched it straight away."
    ];

    private readonly Dictionary<Guid, Task<List<Episode>>> episodes = [];

    private sealed record Show(Guid Id, string Title, string[] Genres);

    private sealed record Episode(Guid Id, int Season, int Number, DateTimeOffset AirsAtUtc);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var catalog = await CatalogAsync(await SignInAsync(username(1), ct), ct);
        if (catalog.Count < 20)
        {
            throw new CliException($"Only {catalog.Count} shows came back from the catalog through {options.BffUrl}; import more first.");
        }
        Console.WriteLine($"{catalog.Count} shows to choose from; seeding {options.Users} users with seed {options.Seed}.");

        var seeded = new List<int>();
        for (var i = 1; i <= options.Users; i++)
        {
            var rng = new Random(options.Seed * 7919 + i);
            // A user takes seconds; one token (15 minutes) is plenty for it.
            var token = await SignInAsync(username(i), ct);
            var summary = await SeedUserAsync(token, i, rng, catalog, ct);
            if (summary is not null)
            {
                Console.WriteLine($"{username(i)}: {summary}");
                seeded.Add(i);
            }
        }

        // Fresh tokens: the seeding above can outlast one. Social knows a person once they've visited it (Identity's
        // profile event can lag behind the watches), so everyone visits, which also gives their id; then each user
        // seeded now follows three to six of all the users, earlier runs' included.
        var people = new List<(int Index, string Token, string UserId)>();
        for (var i = 1; i <= options.Users; i++)
        {
            var token = await SignInAsync(username(i), ct);
            using var me = await GetJsonAsync(token, "/social/api/v1/me/social", ct);
            if (me?.RootElement.GetProperty("userId").GetString() is { } userId)
            {
                people.Add((i, token, userId));
            }
        }
        foreach (var (index, token, userId) in people.Where(p => seeded.Contains(p.Index)))
        {
            var rng = new Random(options.Seed * 104729 + index);
            foreach (var other in people.Where(p => p.UserId != userId).OrderBy(_ => rng.Next()).Take(rng.Next(3, 7)))
            {
                await SendAsync(token, HttpMethod.Post, $"/social/api/v1/people/{other.UserId}/follow", null, ct);
            }
        }
        var skipped = options.Users - seeded.Count;
        Console.WriteLine($"Seeded {seeded.Count} users{(skipped > 0 ? $", skipped {skipped} already seeded" : "")}.");
        return 0;
    }

    /// <summary>
    /// Seeds one user, or returns null when they're done already (their profile has the name this seed gives them).
    /// </summary>
    private async Task<string?> SeedUserAsync(string token, int index, Random rng, List<Show> catalog, CancellationToken ct)
    {
        var persona = Personas[rng.Next(Personas.Length)];
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Zones[rng.Next(Zones.Length)]);
        var name = $"{FirstNames[(index - 1) % FirstNames.Length]} {(char)('A' + rng.Next(26))}.";
        using (var profile = await GetJsonAsync(token, "/identity/api/v1/profile", ct))
        {
            if (profile?.RootElement.GetProperty("displayName").GetString() == name)
            {
                return null;
            }
        }

        double Affinity(Show show) => show.Genres.Count(persona.Genres.Contains) / (double)Math.Max(1, show.Genres.Length);
        var picks = catalog
            .Select(s => (Show: s, Score: Affinity(s) * 3 + rng.NextDouble()))
            .OrderByDescending(x => x.Score)
            .Take(rng.Next(8, 21))
            .Select(x => x.Show)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        int watched = 0, rated = 0, commented = 0;
        foreach (var show in picks)
        {
            var all = await EpisodesAsync(token, show.Id, ct);
            var aired = all.Where(e => e.AirsAtUtc < now.AddHours(-1)).ToList();
            var roll = rng.NextDouble();
            var (status, share) = aired.Count == 0 || roll < 0.2 ? (1, 0.0)            // planned
                : roll < 0.5 ? (2, 1.0)                                                   // finished
                : roll < 0.85 ? (0, 0.2 + rng.NextDouble() * 0.7)                         // watching
                : (4, 0.05 + rng.NextDouble() * 0.35);                                    // stopped

            var watches = History(aired.Take((int)Math.Round(aired.Count * share)).ToList(), zone, now, rng);
            if (status == 2 && watches.Count < aired.Count)
            {
                status = 0;   // the evenings ran out before the episodes did: still watching
            }
            // Watches first: the import follows a new show from its first watch, so the follow keeps that date.
            foreach (var chunk in watches.Chunk(500))
            {
                var answer = await SendAsync(token, HttpMethod.Post, "/tracking/api/v1/watched/import",
                    new { watches = chunk.Select(w => new { episodeId = w.Episode.Id, watchedAtUtc = w.At }) }, ct);
                if (answer == 404)
                {
                    throw new CliException("The history import answered 404: turn the history-import flag on (Flags/flagd.json or the flags page).");
                }
                watched += answer is >= 200 and < 300 ? chunk.Length : 0;
            }
            if (watches.Count == 0)
            {
                await SendAsync(token, HttpMethod.Post, $"/tracking/api/v1/follows/{show.Id}", new { status }, ct);
            }
            else
            {
                // The import follows a new show as watching; put back what this user did with it.
                await SendAsync(token, HttpMethod.Put, $"/tracking/api/v1/follows/{show.Id}/status", new { status }, ct);
            }

            if (watches.Count >= 3 && rng.NextDouble() < 0.75)
            {
                var stars = Stars(show, Affinity(show), status == 4, rng);
                await SendAsync(token, HttpMethod.Put, $"/ratings/api/v1/shows/{show.Id}/rating", new { stars }, ct);
                rated++;
                foreach (var (episode, _) in watches.OrderBy(_ => rng.Next()).Take(rng.Next(0, 3)))
                {
                    var episodeStars = Math.Clamp(stars + rng.Next(-1, 2), 1, 5);
                    await SendAsync(token, HttpMethod.Put, $"/ratings/api/v1/episodes/{episode.Id}/rating", new { showId = show.Id, stars = episodeStars }, ct);
                }
            }
            if (watches.Count > 0 && rng.NextDouble() < 0.15 && commented < 4)
            {
                var (episode, _) = watches[rng.Next(watches.Count)];
                await SendAsync(token, HttpMethod.Post, $"/ratings/api/v1/episodes/{episode.Id}/comments",
                    new { showId = show.Id, text = Comments[rng.Next(Comments.Length)], isSpoiler = false }, ct);
                commented++;
            }
        }
        // Last, as the mark that this user is done.
        await SendAsync(token, HttpMethod.Put, "/identity/api/v1/profile",
            new { displayName = name, avatarUrl = (string?)null, timeZone = zone.Id, region = "US", language = "en" }, ct);
        return $"{name}, {persona.Name}, {zone.Id}: {picks.Count} shows, {watched} watches, {rated} rated, {commented} comments";
    }

    /// <summary>
    /// When each episode was watched: from a start up to <see cref="SeedOptions.Months"/> back, an evening (19:00-23:00
    /// local) every few days, sometimes two to four in one sitting; never before it aired, never in the future.
    /// </summary>
    private List<(Episode Episode, DateTimeOffset At)> History(List<Episode> episodes, TimeZoneInfo zone, DateTimeOffset now, Random rng)
    {
        var result = new List<(Episode, DateTimeOffset)>();
        var at = now.AddDays(-rng.Next(7, Math.Max(8, options.Months * 30)));
        var sitting = 0;
        foreach (var episode in episodes)
        {
            if (sitting > 0)
            {
                at = at.AddMinutes(45 + rng.Next(20));
                sitting--;
            }
            else
            {
                var day = TimeZoneInfo.ConvertTime(at, zone).Date.AddDays(rng.Next(1, 5));
                var evening = new DateTime(day.Year, day.Month, day.Day, 19 + rng.Next(4), rng.Next(60), 0, DateTimeKind.Unspecified);
                at = new DateTimeOffset(evening, zone.GetUtcOffset(evening));
                sitting = rng.NextDouble() < 0.25 ? rng.Next(1, 4) : 0;
            }
            if (at < episode.AirsAtUtc)
            {
                at = episode.AirsAtUtc.AddHours(2 + rng.Next(48));
            }
            if (at > now.AddMinutes(-30))
            {
                break;
            }
            result.Add((episode, at));
        }
        return result;
    }

    /// <summary>
    /// 1-5 stars: the show's own quality (the same for everyone), the persona's liking for its genres, and a little
    /// noise; a show the user stopped loses one.
    /// </summary>
    private static int Stars(Show show, double affinity, bool stopped, Random rng)
    {
        var quality = 2.5 + (BitConverter.ToUInt16(show.Id.ToByteArray(), 0) % 200) / 100.0;   // 2.5-4.5
        var value = quality + affinity - 0.3 + (rng.NextDouble() - 0.5) * 0.8 - (stopped ? 1 : 0);
        return Math.Clamp((int)Math.Round(value), 1, 5);
    }

    /// <summary>
    /// The catalog's shows across the personas' genres (the first pages of each), with their genres.
    /// </summary>
    private async Task<List<Show>> CatalogAsync(string token, CancellationToken ct)
    {
        var shows = new Dictionary<Guid, Show>();
        foreach (var genre in Personas.SelectMany(p => p.Genres).Distinct())
        {
            using var json = await GetJsonAsync(token, $"/catalog/api/v1/genres/{Uri.EscapeDataString(genre)}/shows?pageSize=40", ct);
            if (json is null || !json.RootElement.TryGetProperty("items", out var items))
            {
                continue;
            }
            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("id").GetGuid();
                shows.TryAdd(id, new Show(id, item.GetProperty("title").GetString() ?? "",
                    item.GetProperty("genres").EnumerateArray().Select(g => g.GetString() ?? "").ToArray()));
            }
        }
        return shows.Values.OrderBy(s => s.Id).ToList();
    }

    /// <summary>
    /// A show's dated regular episodes in order, every season (a show is only finished with all of them), loaded once
    /// for every user.
    /// </summary>
    private Task<List<Episode>> EpisodesAsync(string token, Guid showId, CancellationToken ct)
    {
        lock (episodes)
        {
            if (!episodes.TryGetValue(showId, out var load))
            {
                episodes[showId] = load = LoadAsync();
            }
            return load;
        }

        async Task<List<Episode>> LoadAsync()
        {
            using var show = await GetJsonAsync(token, $"/catalog/api/v1/shows/{showId}", ct);
            if (show is null)
            {
                return [];
            }
            var list = new List<Episode>();
            foreach (var season in show.RootElement.GetProperty("seasons").EnumerateArray().Select(s => s.GetProperty("number").GetInt32()).Where(n => n > 0).Order())
            {
                using var detail = await GetJsonAsync(token, $"/catalog/api/v1/shows/{showId}/seasons/{season}", ct);
                if (detail is null)
                {
                    continue;
                }
                list.AddRange(detail.RootElement.GetProperty("episodes").EnumerateArray()
                    .Where(e => e.TryGetProperty("airsAtUtc", out var a) && a.ValueKind == JsonValueKind.String)
                    .Select(e => new Episode(e.GetProperty("id").GetGuid(), season, e.GetProperty("episodeNumber").GetInt32(),
                        e.GetProperty("airsAtUtc").GetDateTimeOffset())));
            }
            return list.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
        }
    }

    private async Task<string> SignInAsync(string name, CancellationToken ct) =>
        (await keycloak.SignInAsync(name, options.Password, ct)).Token;

    private async Task<JsonDocument?> GetJsonAsync(string token, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.BffUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)) : null;
    }

    /// <summary>
    /// Sends a request and returns its status; failures other than 409 (already there) are written to stderr.
    /// </summary>
    private async Task<int> SendAsync(string token, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, options.BffUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 409)
        {
            Console.Error.WriteLine($"  {method} {path}: {(int)response.StatusCode} {Shorten(await response.Content.ReadAsStringAsync(ct))}");
        }
        return (int)response.StatusCode;
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : string.Concat(text.AsSpan(0, 160), "…");
}
