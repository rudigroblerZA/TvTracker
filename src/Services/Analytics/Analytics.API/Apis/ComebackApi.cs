using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// "Is it worth going back?" (see <see cref="Comeback"/>), computed on request from the caller's own watches.
/// </summary>
public static class ComebackApi
{
    public static void MapComebackApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/{showId:guid}/comeback", GetOneAsync).RequireFeature(FeatureFlags.WorthGoingBack);

    /// <summary>
    /// 204 unless the user dropped the show.
    /// </summary>
    private static async Task<Results<Ok<ComebackDto>, NoContent>> GetOneAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var load = await ShowLoads.LoadAsync(db, user.GetUserId(), showId, now, withInsights: true, ct);
        return load is not null && Comeback.Build(load, now) is { } comeback ? TypedResults.Ok(comeback) : TypedResults.NoContent();
    }

    /// <summary>
    /// Every show the user dropped, most recently dropped first.
    /// </summary>
    internal static async Task<List<ComebackDto>> AllAsync(AnalyticsContext db, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.UtcDateTime.AddDays(-InsightRules.StoppedAfterDays);
        var paused = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .GroupBy(w => w.ShowId)
            .Select(g => new { ShowId = g.Key, Last = g.Max(w => w.WatchedAtUtc) })
            .Where(g => g.Last < cutoff)
            .OrderByDescending(g => g.Last)
            .ToListAsync(ct);

        // Finished shows have nothing left to go back to, so they're left out before loading (one aggregate), rather than
        // loading every episode, watch and document of a long history to drop them in Build.
        var ids = paused.Select(p => p.ShowId).ToList();
        var progress = await Viewer.ProgressAsync(db, userId, now, ids, ct);
        ids = ids.Where(id => progress.GetValueOrDefault(id) is not { AllWatched: true }).ToList();
        var loads = await ShowLoads.LoadAsync(db, userId, ids, now, withInsights: true, ct);
        return paused.Select(p => loads.GetValueOrDefault(p.ShowId)).OfType<ShowLoad>()
            .Select(load => Comeback.Build(load, now)).OfType<ComebackDto>().ToList();
    }
}
