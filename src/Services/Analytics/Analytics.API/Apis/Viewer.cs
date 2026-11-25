using Analytics.Data;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

/// <summary>
/// What the on-request endpoints share about the caller: their time zone and their progress through their shows.
/// </summary>
public static class Viewer
{
    /// <summary>
    /// The viewer's time zone, from the profile's IANA id the client sends (<c>tz</c>); UTC when it's missing or unknown.
    /// </summary>
    public static TimeZoneInfo Zone(string? tz) =>
        tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found) ? found : TimeZoneInfo.Utc;

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
}

/// <summary>
/// The stored show documents, read through the shared contract (<see cref="InsightDocuments"/>).
/// </summary>
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

        return showIds.Select(id => (Id: id, Next: NextPremiere.Of(episodes[id], announced.GetValueOrDefault(id), now, premieresOnly)))
            .Where(x => x.Next is not null)
            .ToDictionary(x => x.Id, x => x.Next!);
    }
}

/// <summary>
/// Loads <see cref="ShowLoad"/>s for a set of shows in eight queries whatever their number, so the list endpoints (every
/// dropped show, every refresher due, every catch-up) stop running five or six queries per show.
/// </summary>
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
