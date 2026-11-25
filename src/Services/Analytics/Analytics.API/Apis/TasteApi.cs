using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

/// <summary>
/// Your taste (see <see cref="Taste"/>), computed on request from the caller's own ratings and watches.
/// </summary>
public static class TasteApi
{
    public static void MapTasteApi(this RouteGroupBuilder api) => api.MapGet("/me/taste", GetTasteAsync);

    /// <param name="tz">The caller's time zone (IANA id, from their profile), for the months; UTC when missing or unknown.</param>
    private static async Task<Ok<TasteDto>> GetTasteAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, string? tz = null, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var zone = Viewer.Zone(tz);

        var showRatings = await db.ShowRatings.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Join(db.Shows, r => r.ShowId, s => s.ShowId, (r, s) => new { r.Stars, Show = s })
            .ToListAsync(ct);
        var episodeComparisons = await db.EpisodeRatings.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Join(db.Episodes, r => r.EpisodeId, e => e.EpisodeId, (r, e) => new { r.Stars, e.ExternalRating })
            .Where(x => x.ExternalRating != null)
            .ToListAsync(ct);
        var watches = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .Join(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, e) => new { w.ShowId, w.WatchedAtUtc, e.RuntimeMinutes })
            .ToListAsync(ct);

        var rated = showRatings.Select(r => (r.Stars, r.Show)).ToList();
        var showIds = watches.Select(w => w.ShowId).Distinct().ToList();
        var progress = await Viewer.ProgressAsync(db, userId, now, showIds, ct);
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        return TypedResults.Ok(new TasteDto(
            Taste.Bias(rated, episodeComparisons.Select(r => r.Stars * 2 - r.ExternalRating!.Value)),
            Taste.Genres(rated),
            Taste.Months(watches.Select(w => (ViewerRules.Local(w.WatchedAtUtc, zone), w.RuntimeMinutes)), ViewerRules.LocalDate(now, zone)),
            Taste.Progress(watches.Select(w => (w.ShowId, w.WatchedAtUtc)), progress, shows, now)));
    }
}
