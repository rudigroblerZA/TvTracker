using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Spoiler watch (see <see cref="SpoilerWatch"/>), computed on request from the caller's follows and watches.
/// </summary>
public static class SpoilerWatchApi
{
    public static void MapSpoilerWatchApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/spoiler-watch", GetAsync).RequireFeature(FeatureFlags.SpoilerWatch);

    private static async Task<Ok<SpoilerWatchDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var since = now.AddDays(-SpoilerWatch.WindowDays);

        // The viewer's shows: followed or started.
        var followed = db.Follows.Where(f => f.UserId == userId).Select(f => f.ShowId);
        var watchedShows = db.Watches.Where(w => w.UserId == userId).Select(w => w.ShowId);
        var recent = await db.Episodes.AsNoTracking().AiredRegular(now)
            .Where(e => e.AirsAtUtc >= since && (followed.Contains(e.ShowId) || watchedShows.Contains(e.ShowId)))
            .Select(e => new SpoilerWatch.Recent(e.ShowId, e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.AirsAtUtc!.Value))
            .ToListAsync(ct);
        if (recent.Count == 0)
        {
            return TypedResults.Ok(new SpoilerWatchDto([]));
        }

        var showIds = recent.Select(e => e.ShowId).Distinct().ToList();
        var recentIds = recent.Select(e => e.EpisodeId).ToList();
        var seen = (await db.Watches.AsNoTracking().Where(w => w.UserId == userId && recentIds.Contains(w.EpisodeId))
            .Select(w => w.EpisodeId).ToListAsync(ct)).ToHashSet();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        // A season's last listed episode is its finale once nothing more of the season is still to air.
        var seasons = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0)
            .GroupBy(e => new { e.ShowId, e.SeasonNumber })
            .Select(g => new { g.Key.ShowId, g.Key.SeasonNumber, Last = g.Max(e => e.EpisodeNumber), Unaired = g.Count(e => e.AirsAtUtc == null || e.AirsAtUtc > now) })
            .ToListAsync(ct);
        var finales = seasons.Where(s => s.Unaired == 0).ToDictionary(s => (s.ShowId, s.SeasonNumber), s => s.Last);
        var progress = await Viewer.ProgressAsync(db, userId, now, showIds, ct);

        return TypedResults.Ok(new SpoilerWatchDto(SpoilerWatch.Of(recent.Where(e => !seen.Contains(e.EpisodeId)), shows, finales, progress, now)));
    }
}
