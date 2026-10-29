using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="Status">"on-track" (watched in the last <see cref="FinishOutlookApi.OnTrackDays"/> days), "slowing", or
/// "stalled" (nothing for <see cref="InsightRules.StoppedAfterDays"/> days, the "dropped" of your taste).</param>
/// <param name="EpisodesPerWeek">Your pace on this show over the last four weeks; 0 when you haven't watched it in that time.</param>
/// <param name="FinishBy">When you'd finish what has aired at that pace; null without a pace.</param>
/// <param name="Record">How you did with your other shows from this far in; null with too few to go on.</param>
/// <param name="DropOffAhead">Where most of our viewers stop, when it's still ahead of you; null otherwise.</param>
/// <param name="GetsGoodAhead">Where a slow-starting show gets good, when you haven't got there yet; null otherwise.</param>
public record FinishOutlookDto(
    Guid ShowId,
    string Title,
    string? PosterUrl,
    int Watched,
    int Aired,
    int MinutesLeft,
    string Status,
    DateTime LastWatchedAtUtc,
    double EpisodesPerWeek,
    DateOnly? FinishBy,
    FinishRecordDto? Record,
    DropOff? DropOffAhead,
    GetsGoodAheadDto? GetsGoodAhead = null);

/// <param name="EpisodesAway">Episodes between your furthest one and it.</param>
/// <param name="MinutesAway">Their runtime.</param>
public record GetsGoodAheadDto(int SeasonNumber, int EpisodeNumber, string Title, int EpisodesAway, int MinutesAway);

/// <param name="Comparable">Your other shows you got at least as far into (as a share of what had aired).</param>
/// <param name="Finished">Of those, how many you watched to the end of what had aired.</param>
public record FinishRecordDto(int Comparable, int Finished);

/// <summary>
/// "Will you finish it?" for the shows you're part-way through, from your own watches only (so, like your taste, it
/// needs no minimum counts), plus where our viewers drop off (which does, and is on the show's insights). It doesn't
/// guess a probability: there's too little history per person for one to be honest. It shows the facts that decide it:
/// your pace and when that gets you to the end, whether you've stalled, your record from this far in, and the episode
/// many people stop at.
/// </summary>
public static class FinishOutlookApi
{
    /// <summary>Watched within this many days: on track.</summary>
    public const int OnTrackDays = 14;

    private const int PaceDays = 28;

    /// <summary>Your record is shown from this many comparable shows.</summary>
    private const int MinComparable = 3;

    public static void MapFinishOutlookApi(this RouteGroupBuilder api)
    {
        api.MapGet("/me/finish-outlook", GetAllAsync).RequireFeature(FeatureFlags.Forecasts);
        api.MapGet("/shows/{showId:guid}/finish-outlook", GetOneAsync).RequireFeature(FeatureFlags.Forecasts);
    }

    private static async Task<Ok<List<FinishOutlookDto>>> GetAllAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(await OutlooksAsync(db, user.GetUserId(), null, time.GetUtcNow(), ct));

    /// <summary>204 when you haven't started the show, or are caught up.</summary>
    private static async Task<Results<Ok<FinishOutlookDto>, NoContent>> GetOneAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var outlooks = await OutlooksAsync(db, user.GetUserId(), showId, time.GetUtcNow(), ct);
        return outlooks.FirstOrDefault(o => o.ShowId == showId) is { } outlook ? TypedResults.Ok(outlook) : TypedResults.NoContent();
    }

    /// <param name="only">One show to describe; the record still looks at all of the user's shows.</param>
    private static async Task<List<FinishOutlookDto>> OutlooksAsync(AnalyticsContext db, string userId, Guid? only, DateTimeOffset now, CancellationToken ct)
    {
        // Progress on every show the user has watched (one aggregate), for the record; episodes only for the shows described.
        var progress = (await Viewer.ProgressAsync(db, userId, now, null, ct)).Values.Where(p => p.Started).ToList();
        var inProgress = progress.Where(p => !p.AllWatched && (only is null || p.ShowId == only)).ToList();
        var ids = inProgress.Select(p => p.ShowId).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        var aired = (await db.Episodes.AsNoTracking().AiredRegular(now).Where(e => ids.Contains(e.ShowId))
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToListAsync(ct))
            .ToLookup(e => e.ShowId);
        var since = now.UtcDateTime.AddDays(-PaceDays);
        var watches = (await db.Watches.AsNoTracking().Where(w => w.UserId == userId && ids.Contains(w.ShowId))
                .Select(w => new { w.ShowId, w.EpisodeId, w.WatchedAtUtc }).ToListAsync(ct))
            .ToLookup(w => w.ShowId);
        var insights = await ShowDocuments.LoadAsync(db, ids, ct);

        return inProgress
            .Where(p => shows.ContainsKey(p.ShowId))
            .Select(p =>
            {
                var show = shows[p.ShowId];
                var ordered = aired[p.ShowId].ToList();
                var watched = watches[p.ShowId].Select(w => w.EpisodeId).ToHashSet();
                var left = ordered.Where(e => !watched.Contains(e.EpisodeId)).ToList();
                var last = watches[p.ShowId].Max(w => w.WatchedAtUtc);
                var days = (now.UtcDateTime - last).TotalDays;
                var perWeek = Math.Round(watches[p.ShowId].Count(w => w.WatchedAtUtc >= since) / (PaceDays / 7.0), 1);
                var share = (double)p.Watched / p.Aired;

                var comparable = progress.Where(o => o.ShowId != p.ShowId && (double)o.Watched / o.Aired >= share).ToList();
                var record = comparable.Count >= MinComparable
                    ? new FinishRecordDto(comparable.Count, comparable.Count(o => o.AllWatched))
                    : null;

                // The drop-off is ahead when it's past the furthest episode the user has watched.
                var (furthest, _) = Viewer.Split(ordered, watched);
                var showInsights = insights.GetValueOrDefault(p.ShowId);
                DropOff? ahead = null;
                if (showInsights?.Viewing?.DropOff is { } dropOff
                    && ordered.FindIndex(e => e.SeasonNumber == dropOff.SeasonNumber && e.EpisodeNumber == dropOff.EpisodeNumber) > furthest)
                {
                    ahead = dropOff;
                }
                var getsGood = Viewer.TurnAhead(ordered, furthest, showInsights?.GetsGood?.Turn, e => e.RuntimeMinutes ?? 0) is { } turn
                    ? new GetsGoodAheadDto(turn.Turn.SeasonNumber, turn.Turn.EpisodeNumber, turn.Turn.Title, turn.EpisodesAway, turn.MinutesAway)
                    : null;

                return new FinishOutlookDto(
                    p.ShowId, show.Title, show.PosterUrl, p.Watched, p.Aired, left.Sum(e => e.RuntimeMinutes ?? 0),
                    days <= OnTrackDays ? "on-track" : days <= InsightRules.StoppedAfterDays ? "slowing" : "stalled",
                    last,
                    perWeek,
                    perWeek > 0 ? DateOnly.FromDateTime(now.UtcDateTime.AddDays(Math.Ceiling(left.Count / perWeek * 7))) : null,
                    record,
                    ahead,
                    getsGood);
            })
            .OrderBy(o => o.Status switch { "on-track" => 0, "slowing" => 1, _ => 2 })
            .ThenByDescending(o => o.LastWatchedAtUtc)
            .ToList();
    }
}
