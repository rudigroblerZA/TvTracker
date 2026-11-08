using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

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
/// each user follows a few of the others with the prefix, earlier runs' included (Keycloak lists them).</item>
/// <item><b>Once:</b> the display name keeps the state: while seeding it names the seed, and the plain name, set only
/// once every write went through, marks a finished user. Seeding again, with any seed, skips finished users and finishes
/// a half-done one with its own seed (imports, follows and ratings are idempotent; a comment may be posted again).</item>
/// </list>
/// </summary>
/// <summary>
/// How a user watches. <see cref="Evenings"/>: an evening every few days, sometimes two to four in a row.
/// <see cref="KeepsUp"/>: each new episode within a day or so of airing (catching up on older ones in the evenings), and
/// every show started is caught up. <see cref="Binges"/>: three to seven episodes a night, late.
/// </summary>
internal enum Pace { Evenings, KeepsUp, Binges }

/// <summary>
/// The shares of shows planned, finished and being watched; the rest are stopped part way.
/// </summary>
internal sealed record Mix(double Planned, double Finished, double Watching)
{
    public static readonly Mix Usual = new(0.2, 0.3, 0.35);
}

public sealed partial class Seeder(HttpClient http, Keycloak keycloak, SeedOptions options, Func<int, string> username,
    Func<CancellationToken, Task<string>> adminToken)
{
    private sealed record Persona(string Name, string[] Genres);

    /// <param name="Airing">Only shows with an episode aired in the last three weeks.</param>
    /// <param name="QuietDays">Nothing watched in the last this many days (a lapsed viewer).</param>
    /// <param name="CommentChance">The chance of a comment on each show watched, up to <paramref name="MaxComments"/>.</param>
    private sealed record Profile(string Name, string Taste, TimeZoneInfo Zone, string[] Genres, int MinShows, int MaxShows,
        int Months, Pace Pace, Mix Mix, bool Airing = false, int QuietDays = 0, double CommentChance = 0.15, int MaxComments = 4);


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

    private readonly Dictionary<Guid, Task<Episodes>> episodes = [];


    /// <summary>
    /// A show's regular episodes in order; <c>Complete</c> is false when loading stopped early (more than a history
    /// could hold), so the show can't be marked finished.
    /// </summary>
    private sealed record Episodes(List<Episode> List, bool Complete);

    /// <param name="Running">Airing or renewed (the catalog's status), so new episodes may come.</param>
    private sealed record Show(Guid Id, string[] Genres, bool Running);

    private sealed record Episode(Guid Id, DateTimeOffset AirsAtUtc);

    private enum Outcome { Skipped, Done, Unfinished }

    /// <summary>
    /// Tracking's statuses (WatchStatus).
    /// </summary>
    private const int Watching = 0, Planned = 1, Finished = 2, Stopped = 4;

    /// <summary>
    /// A write went through: 2xx, or 409 (it was there already).
    /// </summary>
    private static bool Ok(int code) => code is >= 200 and < 300 or 409;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var users = Enumerable.Range(1, options.Users).Select(i => new Session(keycloak, username(i), options.Password, options.BffUrl)).ToList();
        var catalog = await CatalogAsync(users[0], Personas.SelectMany(p => p.Genres), ct);
        Console.WriteLine($"{catalog.Count} shows to choose from; seeding {options.Users} users with seed {options.Seed}.");

        var seeded = new List<(int Index, Session User)>();
        int skipped = 0, unfinished = 0;
        for (var i = 1; i <= options.Users; i++)
        {
            var (outcome, summary) = await SeedUserAsync(users[i - 1], i, catalog, ct);
            if (outcome == Outcome.Skipped)
            {
                skipped++;
                continue;
            }
            Console.WriteLine($"{username(i)}: {summary}{(outcome == Outcome.Done ? "" : " (not finished: run again to finish)")}");
            if (outcome == Outcome.Done)
            {
                seeded.Add((i, users[i - 1]));
            }
            else
            {
                unfinished++;
            }
        }

        if (seeded.Count > 0)
        {
            await FollowEachOtherAsync(seeded, ct);
        }
        Console.WriteLine($"Seeded {seeded.Count} users{(skipped > 0 ? $", skipped {skipped} already seeded" : "")}" +
                          $"{(unfinished > 0 ? $", {unfinished} not finished" : "")}.");
        return unfinished > 0 ? 1 : 0;
    }

    /// <summary>
    /// Each user seeded now follows three to six of everyone with the prefix (Keycloak's ids are the users' ids), earlier
    /// runs' included. Every seeded user visited Social while being seeded, so Social knows them; a prefixed user it
    /// doesn't know (made by setup and never seeded) answers 404 and is passed over for the next.
    /// </summary>
    private async Task FollowEachOtherAsync(List<(int Index, Session User)> seeded, CancellationToken ct)
    {
        var everyone = await keycloak.FindUsersAsync(await adminToken(ct), options.Prefix, ct);
        foreach (var (index, user) in seeded)
        {
            var rng = new Random(options.Seed * 104729 + index);
            var me = username(index);
            var wanted = rng.Next(3, 7);
            foreach (var (_, id) in everyone.Where(u => !string.Equals(u.Username, me, StringComparison.OrdinalIgnoreCase)).OrderBy(_ => rng.Next()))
            {
                if (wanted == 0)
                {
                    break;
                }
                if (Ok(await SendAsync(user, HttpMethod.Post, $"/social/api/v1/people/{id}/follow", null, ct, quiet: 404)))
                {
                    wanted--;
                }
            }
        }
    }

    /// <summary>
    /// Seeds one user and says whether they're done; no summary when they were done already. The profile's name keeps
    /// the state: while seeding it carries the seed ("Ava K. (seeding, seed 1)"), so a rerun with any seed finishes
    /// the same picks; once every write has gone through it's the plain name, the mark that they're done.
    /// </summary>
    private async Task<(Outcome Outcome, string? Summary)> SeedUserAsync(Session user, int index, List<Show> catalog, CancellationToken ct)
    {
        var first = FirstNames[(index - 1) % FirstNames.Length];
        // "Ava K." is done; "Ava K. (seeding, seed 3)" is half done with seed 3.
        if (await SeedToUseAsync(user, $"{Regex.Escape(first)} [A-Z]\\.", null, ct) is not { } seed)
        {
            return (Outcome.Skipped, null);
        }

        var rng = new Random(seed * 7919 + index);
        var persona = Personas[rng.Next(Personas.Length)];
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Zones[rng.Next(Zones.Length)]);
        var name = $"{first} {(char)('A' + rng.Next(26))}.";
        var profile = new Profile(name, persona.Name, zone, persona.Genres, 8, 20, options.Months, Pace.Evenings, Mix.Usual);
        return await SeedProfileAsync(user, profile, seed, rng, catalog, ct);
    }

    /// <summary>
    /// The seed to seed a user with, or null when they're done. The profile's name keeps the state: while seeding it carries
    /// the seed (<paramref name="namePattern"/> then " (seeding, seed 3)"), so a rerun finishes the same picks; the plain
    /// name, set only once every write went through, marks them done. With <paramref name="zone"/>, done also needs that
    /// time zone, which the first write sets: a new account's name comes from Keycloak's and could already be the plain one.
    /// </summary>
    private async Task<int?> SeedToUseAsync(Session user, string namePattern, string? zone, CancellationToken ct)
    {
        using var profile = await GetJsonAsync(user, "/identity/api/v1/profile", ct);
        var current = profile?.RootElement.GetProperty("displayName").GetString() ?? "";
        var state = Regex.Match(current, $"^{namePattern}(?: \\(seeding, seed (-?\\d+)\\))?$");
        if (state.Success && state.Groups[1].Success)
        {
            return int.Parse(state.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        var done = state.Success && (zone is null || profile?.RootElement.GetProperty("timeZone").GetString() == zone);
        return done ? null : options.Seed;
    }

    /// <summary>
    /// Seeds one user as <paramref name="profile"/> describes, marking them done (the plain name) once every write went
    /// through.
    /// </summary>
    private async Task<(Outcome Outcome, string? Summary)> SeedProfileAsync(Session user, Profile profile, int seed, Random rng,
        List<Show> catalog, CancellationToken ct)
    {
        var (name, zone) = (profile.Name, profile.Zone);
        // Every write must go through (409, already there, counts) for the user to be done; a rerun repeats what failed.
        var failures = 0;
        async Task<int> WriteAsync(HttpMethod method, string path, object? body, int? quiet = null)
        {
            var code = await SendAsync(user, method, path, body, ct, quiet);
            if (!Ok(code) && code != quiet)
            {
                failures++;
            }
            return code;
        }
        Task<int> ProfileAsync(string displayName) => WriteAsync(HttpMethod.Put, "/identity/api/v1/profile",
            new { displayName, avatarUrl = (string?)null, timeZone = zone.Id, region = "US", language = "en" });

        await ProfileAsync($"{name} (seeding, seed {seed})");
        // Social knows a person once they've visited it (Identity's profile event can lag far behind the watches).
        await WriteAsync(HttpMethod.Get, "/social/api/v1/me/social", null);

        double Affinity(Show show) => show.Genres.Count(profile.Genres.Contains) / (double)Math.Max(1, show.Genres.Length);
        var wanted = rng.Next(profile.MinShows, profile.MaxShows + 1);
        var ranked = catalog
            .Select(s => (Show: s, Score: Affinity(s) * 3 + rng.NextDouble()))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Show)
            .ToList();
        var now = DateTimeOffset.UtcNow;
        List<Show> picks;
        if (profile.Airing)
        {
            // Running shows with an episode in the last three weeks; the candidates' episodes load together (a long show's
            // list stops before its latest seasons, so it drops out, which is as well for someone keeping up).
            var candidates = ranked.Where(s => s.Running).Take(wanted * 3).ToList();
            var loaded = await Task.WhenAll(candidates.Select(c => EpisodesAsync(user, c.Id, ct)));
            picks = candidates.Zip(loaded)
                .Where(x => x.Second.List.Any(e => e.AirsAtUtc < now.AddHours(-1) && e.AirsAtUtc > now.AddDays(-21)))
                .Select(x => x.First).Take(wanted).ToList();
        }
        else
        {
            picks = ranked.Take(wanted).ToList();
        }

        // Every pick's episodes load together (they draw nothing random); the shows are then seeded in turn.
        var loads = picks.Select(p => EpisodesAsync(user, p.Id, ct)).ToList();
        int watched = 0, rated = 0, commented = 0;
        foreach (var (show, load) in picks.Zip(loads))
        {
            var all = await load;
            var aired = all.List.Where(e => e.AirsAtUtc < now.AddHours(-1)).ToList();
            var roll = rng.NextDouble();
            var mix = profile.Mix;
            var (status, share) = aired.Count == 0 || roll < mix.Planned ? (Planned, 0.0)
                : profile.Pace == Pace.KeepsUp ? (Watching, 1.0)
                : roll < mix.Planned + mix.Finished ? (Finished, 1.0)
                : roll < mix.Planned + mix.Finished + mix.Watching ? (Watching, 0.2 + rng.NextDouble() * 0.7)
                : (Stopped, 0.05 + rng.NextDouble() * 0.35);

            var watches = History(aired.Take((int)Math.Round(aired.Count * share)).ToList(), profile, now, rng);
            if (status == Finished && (watches.Count < aired.Count || !all.Complete))
            {
                status = Watching;   // the evenings ran out before the episodes did: still watching
            }
            // Watches first: the import follows a new show from its first watch, so the follow keeps that date.
            foreach (var chunk in watches.Chunk(500))
            {
                if (await WriteAsync(HttpMethod.Post, "/tracking/api/v1/watched/import",
                        new { watches = chunk.Select(w => new { episodeId = w.Episode.Id, watchedAtUtc = w.At }) }) == 404)
                {
                    throw new CliException("The history import answered 404: turn the history-import flag on (Flags/flagd.json or the flags page).");
                }
            }
            // What Tracking has (episodes its copy lacks are dropped), which ratings and comments stay within.
            var recorded = new HashSet<Guid>();
            if (watches.Count > 0)
            {
                using var progress = await GetJsonAsync(user, $"/tracking/api/v1/progress/{show.Id}", ct);
                if (progress?.RootElement.TryGetProperty("watchedEpisodeIds", out var ids) == true)
                {
                    recorded.UnionWith(ids.EnumerateArray().Select(e => e.GetGuid()));
                }
            }
            watches = watches.Where(w => recorded.Contains(w.Episode.Id)).ToList();
            watched += watches.Count;

            // The import follows a new show as watching (dated by its first watch), so put back what this user did with
            // it; a show the import didn't follow (nothing recorded) is followed now.
            if (watches.Count == 0 || await WriteAsync(HttpMethod.Put, $"/tracking/api/v1/follows/{show.Id}/status", new { status }, quiet: 404) == 404)
            {
                await WriteAsync(HttpMethod.Post, $"/tracking/api/v1/follows/{show.Id}", new { status });
            }

            if (watches.Count >= 3 && rng.NextDouble() < 0.75)
            {
                var stars = Stars(show, Affinity(show), status == Stopped, rng);
                await WriteAsync(HttpMethod.Put, $"/ratings/api/v1/shows/{show.Id}/rating", new { stars });
                rated++;
                foreach (var (episode, _) in watches.OrderBy(_ => rng.Next()).Take(rng.Next(0, 3)))
                {
                    var episodeStars = Math.Clamp(stars + rng.Next(-1, 2), 1, 5);
                    await WriteAsync(HttpMethod.Put, $"/ratings/api/v1/episodes/{episode.Id}/rating", new { showId = show.Id, stars = episodeStars });
                }
            }
            if (watches.Count > 0 && rng.NextDouble() < profile.CommentChance && commented < profile.MaxComments)
            {
                var (episode, _) = watches[rng.Next(watches.Count)];
                await WriteAsync(HttpMethod.Post, $"/ratings/api/v1/episodes/{episode.Id}/comments",
                    new { showId = show.Id, text = Comments[rng.Next(Comments.Length)], isSpoiler = false });
                commented++;
            }
        }
        // Last, and only when everything went through, the mark that this user is done.
        var done = failures == 0 && Ok(await ProfileAsync(name));
        return (done ? Outcome.Done : Outcome.Unfinished, $"{name}, {profile.Taste}, {zone.Id}: {picks.Count} shows, {watched} watches, {rated} rated, {commented} comments");
    }

    /// <summary>
    /// When each episode was watched, by the profile's pace: from a start up to its months back (and before its quiet
    /// days, for a lapsed viewer), evenings in the user's time zone; never before an episode aired, never in the future.
    /// </summary>
    private static List<(Episode Episode, DateTimeOffset At)> History(List<Episode> episodes, Profile profile, DateTimeOffset now, Random rng)
    {
        var result = new List<(Episode, DateTimeOffset)>();
        var end = now.AddDays(-profile.QuietDays).AddMinutes(-30);
        var span = Math.Max(8, profile.Months * 30 - profile.QuietDays);
        var start = end.AddDays(-rng.Next(7, span));
        var at = start;
        var sitting = 0;
        foreach (var episode in episodes)
        {
            if (profile.Pace == Pace.KeepsUp && episode.AirsAtUtc >= start && episode.AirsAtUtc.AddHours(2) > at)
            {
                at = episode.AirsAtUtc.AddHours(2 + rng.Next(28));   // within a day or so of airing
            }
            else if (sitting > 0)
            {
                at = at.AddMinutes(45 + rng.Next(20));
                sitting--;
            }
            else
            {
                var binge = profile.Pace == Pace.Binges;
                var day = TimeZoneInfo.ConvertTime(at, profile.Zone).Date.AddDays(rng.Next(1, binge ? 3 : 5));
                var hour = binge ? 21 + rng.Next(3) : 19 + rng.Next(4);
                var evening = new DateTime(day.Year, day.Month, day.Day, hour, rng.Next(60), 0, DateTimeKind.Unspecified);
                at = new DateTimeOffset(evening, profile.Zone.GetUtcOffset(evening));
                sitting = binge ? rng.Next(2, 7) : rng.NextDouble() < 0.25 ? rng.Next(1, 4) : 0;
            }
            if (at < episode.AirsAtUtc)
            {
                at = episode.AirsAtUtc.AddHours(2 + rng.Next(48));
            }
            if (at > end)
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
    /// The catalog's shows across the given genres (the first pages of each), with their genres.
    /// </summary>
    private async Task<List<Show>> CatalogAsync(Session user, IEnumerable<string> genres, CancellationToken ct)
    {
        var pages = await Task.WhenAll(genres.Distinct().Select(async genre =>
        {
            using var json = await GetJsonAsync(user, $"/catalog/api/v1/genres/{Uri.EscapeDataString(genre)}/shows?pageSize=40", ct);
            return json is not null && json.RootElement.TryGetProperty("items", out var items)
                ? items.EnumerateArray().Select(item => new Show(item.GetProperty("id").GetGuid(),
                    item.GetProperty("genres").EnumerateArray().Select(g => g.GetString() ?? "").ToArray(),
                    item.GetProperty("status").GetInt32() is 1 or 2)).ToList()
                : [];
        }));
        var catalog = pages.SelectMany(p => p).DistinctBy(s => s.Id).OrderBy(s => s.Id).ToList();
        return catalog.Count >= 20 ? catalog
            : throw new CliException($"Only {catalog.Count} shows came back from the catalog through {options.BffUrl}; import more first.");
    }

    /// <summary>
    /// A show's dated regular episodes in order, loaded once for every user: season by season until there are more than a
    /// history of <see cref="SeedOptions.Months"/> could hold (two a day), so a 30-season show costs a few requests, not
    /// thirty, and is then never finished.
    /// </summary>
    private Task<Episodes> EpisodesAsync(Session user, Guid showId, CancellationToken ct)
    {
        lock (episodes)
        {
            if (!episodes.TryGetValue(showId, out var load))
            {
                episodes[showId] = load = LoadAsync();
            }
            return load;
        }

        async Task<Episodes> LoadAsync()
        {
            using var show = await GetJsonAsync(user, $"/catalog/api/v1/shows/{showId}", ct);
            if (show is null)
            {
                return new([], false);
            }
            var cap = options.Months * 30 * 2;
            var list = new List<Episode>();
            var seasons = show.RootElement.GetProperty("seasons").EnumerateArray().Select(s => s.GetProperty("number").GetInt32()).Where(n => n > 0).Order().ToList();
            foreach (var season in seasons)
            {
                if (list.Count > cap)
                {
                    return new(list, false);
                }
                using var detail = await GetJsonAsync(user, $"/catalog/api/v1/shows/{showId}/seasons/{season}", ct);
                if (detail is null)
                {
                    // Not kept: the next user to pick the show loads it again, rather than every user getting it cut short.
                    Console.Error.WriteLine($"  GET season {season} of {showId} failed; the show counts as unfinished this time.");
                    lock (episodes)
                    {
                        episodes.Remove(showId);
                    }
                    return new(list, false);
                }
                // Seasons are walked in order, so sorting each keeps the whole list in order on every return.
                list.AddRange(detail.RootElement.GetProperty("episodes").EnumerateArray()
                    .Where(e => e.TryGetProperty("airsAtUtc", out var a) && a.ValueKind == JsonValueKind.String)
                    .OrderBy(e => e.GetProperty("episodeNumber").GetInt32())
                    .Select(e => new Episode(e.GetProperty("id").GetGuid(), e.GetProperty("airsAtUtc").GetDateTimeOffset())));
            }
            return new(list, true);
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(Session user, string path, CancellationToken ct)
    {
        using var request = await user.RequestAsync(HttpMethod.Get, path, null, ct);
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)) : null;
    }

    /// <summary>
    /// Sends a request and returns its status; failures other than 409 (already there) and <paramref name="quiet"/>
    /// (an answer the caller expects) are written to stderr.
    /// </summary>
    private async Task<int> SendAsync(Session user, HttpMethod method, string path, object? body, CancellationToken ct, int? quiet = null)
    {
        using var request = await user.RequestAsync(method, path, body, ct);
        using var response = await http.SendAsync(request, ct);
        var code = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode && code != 409 && code != quiet)
        {
            Console.Error.WriteLine($"  {method} {path}: {code} {Shorten(await response.Content.ReadAsStringAsync(ct))}");
        }
        return code;
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : string.Concat(text.AsSpan(0, 160), "…");
}
