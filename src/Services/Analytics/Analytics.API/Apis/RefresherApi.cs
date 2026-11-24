using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// "Previously on…" (see <see cref="Refresher"/>), computed on request from the caller's own watches.
/// </summary>
public static class RefresherApi
{
    /// <summary>
    /// The list on Up Next looks at this many of the user's most recently paused shows.
    /// </summary>
    private const int MinBudget = 30;
    private const int MaxBudget = 600;

    private const int MaxShows = 40;

    public static void MapRefresherApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{showId:guid}/refresher", GetOneAsync).RequireFeature(FeatureFlags.Refresher);
        api.MapGet("/me/refreshers", GetAllAsync).RequireFeature(FeatureFlags.Refresher);
    }

    /// <summary>
    /// 204 when the user hasn't watched the show, or has seen all of it and nothing more is coming.
    /// </summary>
    private static async Task<Results<Ok<RefresherDto>, NoContent, ValidationProblem>> GetOneAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, int minutes = Refresher.DefaultBudget, CancellationToken ct = default)
    {
        if (minutes is < MinBudget or > MaxBudget)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["minutes"] = [$"Use {MinBudget} to {MaxBudget} minutes."] });
        }

        var now = time.GetUtcNow();
        var load = await ShowLoads.LoadAsync(db, user.GetUserId(), showId, now, withInsights: false, ct);
        if (load is null)
        {
            return TypedResults.NoContent();
        }
        var returns = await NextPremieres.FindAsync(db, [showId], load.Episodes.ToLookup(e => e.ShowId), now, premieresOnly: false, ct);
        return Refresher.Build(load, returns.GetValueOrDefault(showId), minutes, now) is { } refresher ? TypedResults.Ok(refresher) : TypedResults.NoContent();
    }

    /// <summary>
    /// Followed shows due a refresher: the ones coming back soonest first, then the most recently paused.
    /// </summary>
    private static async Task<Ok<List<RefresherDto>>> GetAllAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var cutoff = now.UtcDateTime.AddDays(-Refresher.GapDays);
        var followed = db.Follows.Where(f => f.UserId == userId).Select(f => f.ShowId);
        var paused = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId && followed.Contains(w.ShowId))
            .GroupBy(w => w.ShowId)
            .Select(g => new { ShowId = g.Key, Last = g.Max(w => w.WatchedAtUtc) })
            .Where(g => g.Last < cutoff)
            .OrderByDescending(g => g.Last)
            .Take(MaxShows)
            .ToListAsync(ct);

        var ids = paused.Select(p => p.ShowId).ToList();
        var loads = await ShowLoads.LoadAsync(db, userId, ids, now, withInsights: false, ct);
        var returns = await NextPremieres.FindAsync(db, ids, loads.Values.SelectMany(l => l.Episodes).ToLookup(e => e.ShowId), now, premieresOnly: false, ct);
        var due = ids.Select(id => loads.GetValueOrDefault(id)).OfType<ShowLoad>()
            .Select(load => Refresher.Build(load, returns.GetValueOrDefault(load.Show.ShowId), Refresher.DefaultBudget, now))
            .OfType<RefresherDto>()
            .Where(r => r.Due)
            .ToList();

        return TypedResults.Ok(due
            .OrderBy(r => r.Returns is null)
            .ThenBy(r => r.Returns?.PremieresAtUtc)
            .ThenByDescending(r => r.LastWatchedAtUtc)
            .ToList());
    }
}
