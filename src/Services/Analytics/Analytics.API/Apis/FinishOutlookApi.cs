using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// The finish outlook (see <see cref="FinishOutlook"/>), computed on request from the caller's own watches.
/// </summary>
public static class FinishOutlookApi
{
    public static void MapFinishOutlookApi(this RouteGroupBuilder api)
    {
        api.MapGet("/me/finish-outlook", GetAllAsync).RequireFeature(FeatureFlags.Forecasts);
        api.MapGet("/shows/{showId:guid}/finish-outlook", GetOneAsync).RequireFeature(FeatureFlags.Forecasts);
    }

    private static async Task<Ok<List<FinishOutlookDto>>> GetAllAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(await OutlooksAsync(db, user.GetUserId(), null, time.GetUtcNow(), ct));

    /// <summary>
    /// 204 when you haven't started the show, or are caught up.
    /// </summary>
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
        var watches = (await db.Watches.AsNoTracking().Where(w => w.UserId == userId && ids.Contains(w.ShowId))
                .Select(w => new { w.ShowId, w.EpisodeId, w.WatchedAtUtc }).ToListAsync(ct))
            .ToLookup(w => w.ShowId, w => (w.EpisodeId, w.WatchedAtUtc));
        var insights = await ShowDocuments.LoadAsync(db, ids, ct);

        return FinishOutlook.Of(inProgress, progress, shows, aired, watches, insights, now);
    }
}
