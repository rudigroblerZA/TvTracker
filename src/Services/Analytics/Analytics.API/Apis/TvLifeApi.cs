using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Your life in TV (see <see cref="TvLife"/>), computed on request from the catalog copy and the caller's own watches.
/// </summary>
public static class TvLifeApi
{
    public static void MapTvLifeApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/tv-life", GetAsync).RequireFeature(FeatureFlags.TvLife);

    /// <param name="born">The viewer's birth year.</param>
    /// <param name="tz">The caller's time zone (IANA id, from their profile); UTC when missing or unknown.</param>
    private static async Task<Results<Ok<TvLifeDto>, ValidationProblem>> GetAsync(
        int born, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, string? tz = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (born < TvLife.EarliestBirthYear || born > now.Year)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["born"] = [$"Use a year from {TvLife.EarliestBirthYear} to {now.Year}."] });
        }
        var userId = user.GetUserId();
        var zone = Viewer.Zone(tz);

        // Every catalog show's first and last regular episode, from one aggregate.
        var runs = await db.Episodes.AsNoTracking().AiredRegular(now)
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, First = g.Min(e => e.AirsAtUtc)!.Value, Last = g.Max(e => e.AirsAtUtc)!.Value })
            .ToListAsync(ct);
        var shows = await db.Shows.AsNoTracking().ToDictionaryAsync(s => s.ShowId, ct);

        // The viewer's watches in their calendar, bulk marks left out of minutes and firsts (they say when a back catalog was marked).
        var rows = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .Join(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, e) => new { w.ShowId, w.WatchedAtUtc, Minutes = e.RuntimeMinutes ?? 0 })
            .ToListAsync(ct);
        var bulk = ShowFactRules.BulkMarks(rows, r => r.ShowId, r => r.WatchedAtUtc);
        var watches = rows.Where(r => !bulk.Contains((r.ShowId, r.WatchedAtUtc)))
            .Select(r => (r.ShowId, Year: ViewerRules.Local(r.WatchedAtUtc, zone).Year, r.Minutes))
            .ToList();
        var watchedShows = rows.Select(r => r.ShowId).ToHashSet();
        var progress = await Viewer.ProgressAsync(db, userId, now, null, ct);

        var finished = progress.Values.Where(p => p.AllWatched && p.LastWatchedAtUtc is not null)
            .ToLookup(p => ViewerRules.Local(p.LastWatchedAtUtc!.Value, zone).Year, p => p.ShowId);

        return TypedResults.Ok(TvLife.Of(born, now.Year, runs.Select(r => new TvLife.Run(r.ShowId, r.First, r.Last)).ToList(), shows, watches,
            watchedShows, finished));
    }
}
