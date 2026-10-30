using Analytics.Data;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

/// <summary>
/// A viewer's progress on one show, aggregated in SQL: the "computed on request" endpoints used to load every aired
/// episode of every show the user ever watched to count these in memory (a viewer of 150 shows pulled ~15,000 rows
/// per show-page visit). Regular episodes only, as Tracking counts.
/// </summary>
/// <param name="Aired">Aired regular episodes.</param>
/// <param name="Watched">Of those, how many the viewer has watched.</param>
/// <param name="LatestSeason">The latest season with an aired episode.</param>
/// <param name="LatestWatched">The latest aired episode (by season and number) is watched: caught up, allowing skipped episodes.</param>
/// <param name="MinutesLeft">The running time of the unwatched aired episodes (missing runtimes as 0).</param>
/// <param name="LastAiredAtUtc">The latest aired episode's air time.</param>
/// <param name="LastWatchedAtUtc">The viewer's latest watch of the show; null when they've watched none of it.</param>
public sealed record ShowProgress(Guid ShowId, int Aired, int Watched, int LatestSeason, bool LatestWatched, int MinutesLeft,
    DateTimeOffset? LastAiredAtUtc, DateTime? LastWatchedAtUtc)
{
    public bool AllWatched => Aired > 0 && Watched >= Aired;
    public bool Started => Watched > 0;
    public int Left => Aired - Watched;
}

/// <summary>What the on-request endpoints share about the caller: their progress, and the helpers that read it.</summary>
public static class Viewer
{
    /// <summary>
    /// Progress per show, for the given shows or (null) every show the viewer has watched. One query: aired regular
    /// episodes left-joined to the viewer's watches, grouped by show.
    /// </summary>
    public static async Task<Dictionary<Guid, ShowProgress>> ProgressAsync(AnalyticsContext db, string userId, DateTimeOffset now,
        IReadOnlyCollection<Guid>? showIds, CancellationToken ct)
    {
        var mine = db.Watches.Where(w => w.UserId == userId);
        var episodes = db.Episodes.AsNoTracking().AiredRegular(now);
        episodes = showIds is null
            ? episodes.Where(e => mine.Select(w => w.ShowId).Contains(e.ShowId))
            : episodes.Where(e => showIds.Contains(e.ShowId));
        var rows = await episodes
            .GroupJoin(mine, e => e.EpisodeId, w => w.EpisodeId, (e, ws) => new { e, ws })
            .SelectMany(x => x.ws.DefaultIfEmpty(), (x, w) => new { x.e, w })
            .GroupBy(x => x.e.ShowId)
            .Select(g => new
            {
                ShowId = g.Key,
                Aired = g.Count(),
                Watched = g.Count(x => x.w != null),
                LatestSeason = g.Max(x => x.e.SeasonNumber),
                // Season and number packed into one int, so "the latest aired episode is watched" is two maxima.
                LatestAired = g.Max(x => x.e.SeasonNumber * 100000 + x.e.EpisodeNumber),
                LatestWatched = g.Max(x => x.w != null ? x.e.SeasonNumber * 100000 + x.e.EpisodeNumber : -1),
                MinutesLeft = g.Sum(x => x.w == null ? (x.e.RuntimeMinutes ?? 0) : 0),
                LastAired = g.Max(x => x.e.AirsAtUtc),
                LastWatched = g.Max(x => x.w != null ? (DateTime?)x.w.WatchedAtUtc : null)
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.ShowId, r => new ShowProgress(r.ShowId, r.Aired, r.Watched, r.LatestSeason,
            r.Watched > 0 && r.LatestWatched == r.LatestAired, r.MinutesLeft, r.LastAired, r.LastWatched));
    }

    /// <summary>
    /// Where the viewer is in a show's aired episodes (in season order): the index of the furthest one watched (-1 for
    /// none) and everything after it. Episodes skipped earlier on don't count as still to watch.
    /// </summary>
    public static (int Furthest, List<EpisodeCopy> Ahead) Split(List<EpisodeCopy> aired, HashSet<Guid> watched)
    {
        var furthest = aired.FindLastIndex(e => watched.Contains(e.EpisodeId));
        return (furthest, aired.Skip(furthest + 1).ToList());
    }

    /// <summary>
    /// A slow start's turn when it's still ahead of the viewer: the episodes between the furthest watched and the
    /// turn, and their running time; null when the turn is behind them or isn't in the list.
    /// </summary>
    public static (GetsGoodTurn Turn, int EpisodesAway, int MinutesAway)? TurnAhead(List<EpisodeCopy> aired, int furthest, GetsGoodTurn? turn,
        Func<EpisodeCopy, int> minutes)
    {
        if (turn is null || aired.FindIndex(e => e.EpisodeId == turn.EpisodeId) is var at && at <= furthest)
        {
            return null;
        }
        var between = aired.Skip(furthest + 1).Take(at - furthest - 1).ToList();
        return (turn, between.Count, between.Sum(minutes));
    }

    /// <summary>Tonight's plan's overrun, shared by the features that fill a time budget: max(5 min, a twelfth).</summary>
    public static int Overrun(int budgetMinutes) => Math.Max(5, budgetMinutes / 12);
}

/// <summary>The stored show documents, read through the shared contract (<see cref="InsightDocuments"/>).</summary>
public static class ShowDocuments
{
    public static async Task<Dictionary<Guid, ShowInsights>> LoadAsync(AnalyticsContext db, IReadOnlyCollection<Guid> showIds, CancellationToken ct)
    {
        if (showIds.Count == 0)
        {
            return [];
        }
        var rows = await db.ShowInsights.AsNoTracking().Where(i => showIds.Contains(i.ShowId)).ToListAsync(ct);
        return rows.Select(r => (r.ShowId, Insights: InsightDocuments.Read(r))).Where(r => r.Insights is not null)
            .ToDictionary(r => r.ShowId, r => r.Insights!);
    }

    public static async Task<ShowInsights?> LoadAsync(AnalyticsContext db, Guid showId, CancellationToken ct) =>
        (await LoadAsync(db, [showId], ct)).GetValueOrDefault(showId);
}

/// <summary>A show's next season premiere: a scheduled first episode or an announced season, whichever is sooner.</summary>
/// <param name="NewSeason">A season premiere (or an announced season), rather than the next episode of a season already airing.</param>
public sealed record NextPremiere(int SeasonNumber, DateTimeOffset PremieresAtUtc, bool NewSeason);

public static class NextPremieres
{
    /// <summary>
    /// The next thing to air per show, from the shows' listed episodes and the announced seasons: a scheduled season
    /// premiere or announced season, else (when <paramref name="premieresOnly"/> is false) the next episode of the
    /// season under way. Null for a show with nothing scheduled.
    /// </summary>
    public static async Task<Dictionary<Guid, NextPremiere>> FindAsync(AnalyticsContext db, IReadOnlyCollection<Guid> showIds,
        ILookup<Guid, EpisodeCopy> episodes, DateTimeOffset now, bool premieresOnly, CancellationToken ct)
    {
        var announced = (await db.AnnouncedSeasons.AsNoTracking()
                .Where(s => showIds.Contains(s.ShowId) && s.PremieresAtUtc > now)
                .ToListAsync(ct))
            .GroupBy(s => s.ShowId)
            .ToDictionary(g => g.Key, g => g.MinBy(s => s.PremieresAtUtc)!);

        var result = new Dictionary<Guid, NextPremiere>();
        foreach (var showId in showIds)
        {
            var scheduled = episodes[showId].Where(e => e.AirsAtUtc > now && (!premieresOnly || e.EpisodeNumber == 1)).MinBy(e => e.AirsAtUtc);
            var season = announced.GetValueOrDefault(showId);
            var next = (scheduled, season) switch
            {
                ({ } e, { } s) when s.PremieresAtUtc < e.AirsAtUtc => new NextPremiere(s.SeasonNumber, s.PremieresAtUtc!.Value, true),
                ({ } e, _) => new NextPremiere(e.SeasonNumber, e.AirsAtUtc!.Value, e.EpisodeNumber == 1),
                (null, { } s) => new NextPremiere(s.SeasonNumber, s.PremieresAtUtc!.Value, true),
                _ => null
            };
            if (next is not null)
            {
                result[showId] = next;
            }
        }
        return result;
    }
}

/// <summary>
/// Everything an on-request endpoint needs about one show for one viewer: the show, its regular episodes in season
/// order, the viewer's watches and stars, the ratings the essential cut goes by, moderators' notes and the stored
/// document. Loaded for a set of shows in eight queries whatever their number, so the list endpoints (every dropped
/// show, every refresher due, every catch-up) stop running five or six queries per show.
/// </summary>
public sealed class ShowLoad(ShowCopy show, List<EpisodeCopy> episodes, List<UserEpisodeWatch> watches, Dictionary<Guid, int> stars,
    Func<EpisodeCopy, double?> rating, Dictionary<Guid, EpisodeNoteCopy> notes, ShowInsights? insights, DateTimeOffset now)
{
    public ShowCopy Show => show;

    /// <summary>Regular episodes, aired or not, in season order.</summary>
    public List<EpisodeCopy> Episodes => episodes;

    public List<EpisodeCopy> Aired { get; } = episodes.Where(e => e.AirsAtUtc <= now).ToList();

    public List<UserEpisodeWatch> Watches => watches;

    public HashSet<Guid> Watched { get; } = watches.Select(w => w.EpisodeId).ToHashSet();

    /// <summary>The viewer's own stars per episode.</summary>
    public Dictionary<Guid, int> Stars => stars;

    /// <summary>The essential cut's rating of an episode (see <see cref="EssentialCutApi.RatingsAsync"/>).</summary>
    public Func<EpisodeCopy, double?> Rating => rating;

    public Dictionary<Guid, EpisodeNoteCopy> Notes => notes;

    public ShowInsights? Insights => insights;

    /// <summary>Episode runtimes, with missing ones as the show's typical one (see <see cref="EssentialCutApi.MinutesOf"/>).</summary>
    public Func<EpisodeCopy, int> Minutes => field ??= EssentialCutApi.MinutesOf(Aired);
}

public static class ShowLoads
{
    /// <param name="withInsights">Load the shows' documents too (the comeback's turn, the backlog's assessments).</param>
    public static async Task<Dictionary<Guid, ShowLoad>> LoadAsync(AnalyticsContext db, string userId, IReadOnlyCollection<Guid> showIds,
        DateTimeOffset now, bool withInsights, CancellationToken ct)
    {
        if (showIds.Count == 0)
        {
            return [];
        }
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        var episodes = (await db.Episodes.AsNoTracking()
                .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0)
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .ToListAsync(ct))
            .ToLookup(e => e.ShowId);
        var watches = (await db.Watches.AsNoTracking().Where(w => w.UserId == userId && showIds.Contains(w.ShowId)).ToListAsync(ct))
            .ToLookup(w => w.ShowId);
        var stars = (await db.EpisodeRatings.AsNoTracking().Where(r => r.UserId == userId && showIds.Contains(r.ShowId)).ToListAsync(ct))
            .ToLookup(r => r.ShowId);
        var ratings = await EssentialCutApi.RatingsAsync(db, showIds, ct);
        var notes = (await db.EpisodeNotes.AsNoTracking().Where(n => showIds.Contains(n.ShowId)).ToListAsync(ct)).ToLookup(n => n.ShowId);
        var insights = withInsights ? await ShowDocuments.LoadAsync(db, showIds, ct) : [];

        return shows.Values.ToDictionary(s => s.ShowId, s => new ShowLoad(s, episodes[s.ShowId].ToList(), watches[s.ShowId].ToList(),
            stars[s.ShowId].ToDictionary(r => r.EpisodeId, r => r.Stars), ratings(s.ShowId),
            notes[s.ShowId].ToDictionary(n => n.EpisodeId), insights.GetValueOrDefault(s.ShowId), now));
    }

    public static async Task<ShowLoad?> LoadAsync(AnalyticsContext db, string userId, Guid showId, DateTimeOffset now, bool withInsights, CancellationToken ct) =>
        (await LoadAsync(db, userId, [showId], now, withInsights, ct)).GetValueOrDefault(showId);
}
