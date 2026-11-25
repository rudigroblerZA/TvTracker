using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Backlog triage (see <see cref="Backlog"/>), computed on request from the caller's follows and watches.
/// </summary>
public static class BacklogApi
{
    public static void MapBacklogApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/backlog", GetAsync).RequireFeature(FeatureFlags.BacklogTriage);

    private static async Task<Ok<BacklogDto>> GetAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var started = db.Watches.Where(w => w.UserId == userId).Select(w => w.ShowId);
        var notStarted = await db.Follows.AsNoTracking()
            .Where(f => f.UserId == userId && !started.Contains(f.ShowId))
            .Select(f => f.ShowId)
            .ToListAsync(ct);
        var runs = await ShowValueFacts.RunTimesAsync(db, notStarted, now, ct);
        var shows = await db.Shows.AsNoTracking().Where(s => runs.Keys.Contains(s.ShowId)).ToListAsync(ct);
        var insights = await ShowDocuments.LoadAsync(db, runs.Keys.ToList(), ct);
        var liked = await ShowValueFacts.LikedGenresAsync(db, userId, now, ct);

        var assessed = Backlog.Assess(shows, runs, insights, liked);
        // The long shows' episodes, ratings and notes, in one load, for the lean cut's running time.
        var cuts = await ShowLoads.LoadAsync(db, userId, assessed.Where(a => a.Verdict == "long").Select(a => a.Show.ShowId).ToList(), now,
            withInsights: false, ct);
        var dropped = await ComebackApi.AllAsync(db, userId, now, ct);
        return TypedResults.Ok(Backlog.Of(assessed, cuts, dropped, await CatchUpApi.PaceAsync(db, userId, now, ct), now));
    }
}
