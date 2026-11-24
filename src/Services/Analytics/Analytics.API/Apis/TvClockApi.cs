using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Your TV clock (see <see cref="TvClock"/>), from the caller's own watches in the last year, placed in their profile
/// time zone (<c>tz</c>).
/// </summary>
public static class TvClockApi
{
    public static void MapTvClockApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/tv-clock", GetAsync).RequireFeature(FeatureFlags.TvClock);

    /// <param name="tz">The caller's time zone (IANA id, from their profile); UTC when missing or unknown.</param>
    private static async Task<Ok<TvClockDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, string? tz = null,
        CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var zone = Viewer.Zone(tz);
        var now = time.GetUtcNow().UtcDateTime;
        var from = now.AddYears(-1);

        var rows = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId && w.WatchedAtUtc >= from)
            .Join(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, e) => new { w.ShowId, w.WatchedAtUtc, e.RuntimeMinutes })
            .ToListAsync(ct);
        var bulk = ShowFactRules.BulkMarks(rows, r => r.ShowId, r => r.WatchedAtUtc);
        var watches = rows.Where(r => !bulk.Contains((r.ShowId, r.WatchedAtUtc)))
            .Select(r => TvClock.Placed(r.ShowId, r.WatchedAtUtc, r.RuntimeMinutes, zone))
            .OrderBy(w => w.MarkedAtUtc)
            .ToList();
        var since = DateOnly.FromDateTime(watches.Count > 0 ? watches[0].Local : ViewerRules.Local(from, zone));

        // Titles and posters only once there's a clock to show them on.
        var shows = new Dictionary<Guid, (string Title, string? PosterUrl)>();
        if (watches.Count >= TvClock.MinWatches)
        {
            var showIds = watches.Select(w => w.ShowId).Distinct().ToList();
            shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId))
                .Select(s => new { s.ShowId, s.Title, s.PosterUrl })
                .ToDictionaryAsync(s => s.ShowId, s => (s.Title, s.PosterUrl), ct);
        }

        return TypedResults.Ok(TvClock.Of(watches, since, rows.Count - watches.Count, shows));
    }
}
