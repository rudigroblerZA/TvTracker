using System.Security.Claims;
using System.Text.Json;
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

    private static async Task<Results<Ok<List<FinishOutlookDto>>, UnauthorizedHttpResult>> GetAllAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct) =>
        user.FindFirst("sub")?.Value is { } userId
            ? TypedResults.Ok(await OutlooksAsync(db, userId, null, time.GetUtcNow(), ct))
            : TypedResults.Unauthorized();

    /// <summary>204 when you haven't started the show, or are caught up.</summary>
    private static async Task<Results<Ok<FinishOutlookDto>, NoContent, UnauthorizedHttpResult>> GetOneAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var outlooks = await OutlooksAsync(db, userId, showId, time.GetUtcNow(), ct);
        return outlooks.FirstOrDefault(o => o.ShowId == showId) is { } outlook ? TypedResults.Ok(outlook) : TypedResults.NoContent();
    }

    /// <param name="only">One show to describe; the record still looks at all of the user's shows.</param>
    private static async Task<List<FinishOutlookDto>> OutlooksAsync(AnalyticsContext db, string userId, Guid? only, DateTimeOffset now, CancellationToken ct)
    {
        var watches = await db.Watches.AsNoTracking().Where(w => w.UserId == userId).ToListAsync(ct);
        var showIds = watches.Select(w => w.ShowId).Distinct().ToList();
        var aired = (await db.Episodes.AsNoTracking()
                .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
                .ToListAsync(ct))
            .ToLookup(e => e.ShowId);
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        // Progress on every show the user has watched: the share of what's aired, and whether they got to the end.
        var progress = watches.GroupBy(w => w.ShowId)
            .Where(g => aired[g.Key].Any())
            .Select(g =>
            {
                var episodes = aired[g.Key].ToList();
                var ids = g.Select(w => w.EpisodeId).ToHashSet();
                var watched = episodes.Count(e => ids.Contains(e.EpisodeId));
                return (ShowId: g.Key, Watched: watched, Aired: episodes.Count, Episodes: episodes, Ids: ids,
                    Last: g.Max(w => w.WatchedAtUtc), Recent: g.Count(w => w.WatchedAtUtc >= now.UtcDateTime.AddDays(-PaceDays)));
            })
            .Where(p => p.Watched > 0)
            .ToList();

        var inProgress = progress.Where(p => p.Watched < p.Aired && shows.ContainsKey(p.ShowId) && (only is null || p.ShowId == only)).ToList();
        var insights = await InsightsAsync(db, inProgress.Select(p => p.ShowId).ToList(), ct);

        return inProgress
            .Select(p =>
            {
                var show = shows[p.ShowId];
                var left = p.Episodes.Where(e => !p.Ids.Contains(e.EpisodeId)).ToList();
                var days = (now.UtcDateTime - p.Last).TotalDays;
                var perWeek = Math.Round(p.Recent / (PaceDays / 7.0), 1);
                var share = (double)p.Watched / p.Aired;

                var comparable = progress.Where(o => o.ShowId != p.ShowId && (double)o.Watched / o.Aired >= share).ToList();
                var record = comparable.Count >= MinComparable
                    ? new FinishRecordDto(comparable.Count, comparable.Count(o => o.Watched == o.Aired))
                    : null;

                // The drop-off is ahead when it's past the furthest episode the user has watched.
                var ordered = p.Episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
                var furthest = ordered.FindLastIndex(e => p.Ids.Contains(e.EpisodeId));
                var showInsights = insights.GetValueOrDefault(p.ShowId);
                DropOff? ahead = null;
                if (showInsights?.Viewing?.DropOff is { } dropOff
                    && ordered.FindIndex(e => e.SeasonNumber == dropOff.SeasonNumber && e.EpisodeNumber == dropOff.EpisodeNumber) > furthest)
                {
                    ahead = dropOff;
                }

                GetsGoodAheadDto? getsGood = null;
                if (showInsights?.GetsGood?.Turn is { } turn
                    && ordered.FindIndex(e => e.EpisodeId == turn.EpisodeId) is var turnIndex && turnIndex > furthest)
                {
                    var between = ordered.Skip(furthest + 1).Take(turnIndex - furthest - 1).ToList();
                    getsGood = new GetsGoodAheadDto(turn.SeasonNumber, turn.EpisodeNumber, turn.Title, between.Count, between.Sum(e => e.RuntimeMinutes ?? 0));
                }

                return new FinishOutlookDto(
                    p.ShowId, show.Title, show.PosterUrl, p.Watched, p.Aired, left.Sum(e => e.RuntimeMinutes ?? 0),
                    days <= OnTrackDays ? "on-track" : days <= InsightRules.StoppedAfterDays ? "slowing" : "stalled",
                    p.Last,
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

    /// <summary>The shows' insights documents, for their drop-off points (there only with enough viewers) and turns.</summary>
    internal static async Task<Dictionary<Guid, ShowInsights>> InsightsAsync(AnalyticsContext db, List<Guid> showIds, CancellationToken ct)
    {
        var documents = await db.ShowInsights.AsNoTracking().Where(i => showIds.Contains(i.ShowId)).Select(i => new { i.ShowId, i.Document }).ToListAsync(ct);
        return documents
            .Select(d => (d.ShowId, Insights: JsonSerializer.Deserialize<ShowInsights>(d.Document, JsonSerializerOptions.Web)))
            .Where(d => d.Insights is not null)
            .ToDictionary(d => d.ShowId, d => d.Insights!);
    }
}
