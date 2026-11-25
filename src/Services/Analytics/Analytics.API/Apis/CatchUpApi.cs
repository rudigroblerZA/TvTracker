using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// The catch-up planner (see <see cref="CatchUp"/>), computed on request from the caller's own watches.
/// </summary>
public static class CatchUpApi
{
    public static void MapCatchUpApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{showId:guid}/catch-up", GetOneAsync).RequireFeature(FeatureFlags.CatchUp);
        api.MapGet("/me/catch-up", GetAllAsync).RequireFeature(FeatureFlags.CatchUp);
    }

    /// <summary>
    /// 204 when no new season is due within <see cref="CatchUp.HorizonDays"/> days, or the user is already caught up.
    /// </summary>
    private static async Task<Results<Ok<CatchUpDto>, NoContent>> GetOneAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, bool cut = false, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var pace = await PaceAsync(db, userId, now, ct);
        var load = await ShowLoads.LoadAsync(db, userId, showId, now, withInsights: false, ct);
        if (load is null)
        {
            return TypedResults.NoContent();
        }
        var premieres = await NextPremieres.FindAsync(db, [showId], load.Episodes.ToLookup(e => e.ShowId), now, premieresOnly: true, ct);
        return CatchUp.Build(load, premieres.GetValueOrDefault(showId), pace, cut, now) is { } plan ? TypedResults.Ok(plan) : TypedResults.NoContent();
    }

    /// <summary>
    /// Every followed show with a season coming and something to catch up on, soonest premiere first.
    /// </summary>
    private static async Task<Ok<List<CatchUpDto>>> GetAllAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var horizon = now.AddDays(CatchUp.HorizonDays);
        var followed = await db.Follows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.ShowId).ToListAsync(ct);
        var coming = (await db.Episodes.AsNoTracking()
                .Where(e => followed.Contains(e.ShowId) && e.EpisodeNumber == 1 && e.SeasonNumber > 0 && e.AirsAtUtc > now && e.AirsAtUtc <= horizon)
                .Select(e => e.ShowId)
                .Union(db.AnnouncedSeasons.Where(s => followed.Contains(s.ShowId) && s.PremieresAtUtc > now && s.PremieresAtUtc <= horizon).Select(s => s.ShowId))
                .ToListAsync(ct))
            .Distinct().ToList();

        var pace = await PaceAsync(db, userId, now, ct);
        var loads = await ShowLoads.LoadAsync(db, userId, coming, now, withInsights: false, ct);
        var premieres = await NextPremieres.FindAsync(db, coming, loads.Values.SelectMany(l => l.Episodes).ToLookup(e => e.ShowId), now, premieresOnly: true, ct);
        var plans = coming.Select(id => loads.GetValueOrDefault(id)).OfType<ShowLoad>()
            .Select(load => CatchUp.Build(load, premieres.GetValueOrDefault(load.Show.ShowId), pace, false, now))
            .OfType<CatchUpDto>()
            .ToList();

        return TypedResults.Ok(plans.OrderBy(p => p.Premiere.PremieresAtUtc).ToList());
    }

    /// <summary>
    /// Minutes a week the user has watched lately, across all their shows (each episode at its runtime, each day capped
    /// at <see cref="CatchUp.MaxMinutesPerDay"/>).
    /// </summary>
    internal static async Task<int> PaceAsync(AnalyticsContext db, string userId, DateTimeOffset now, CancellationToken ct) =>
        (await PaceDetailsAsync(db, userId, now, ct)).MinutesPerWeek;

    /// <summary>
    /// <see cref="PaceAsync"/> with the number of watches behind it, for callers that say why a pace is missing.
    /// </summary>
    internal static async Task<CatchUp.Pace> PaceDetailsAsync(AnalyticsContext db, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var since = now.UtcDateTime.AddDays(-7 * CatchUp.PaceWeeks);
        var days = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId && w.WatchedAtUtc >= since)
            .Join(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, e) => new { w.WatchedAtUtc.Date, Minutes = e.RuntimeMinutes ?? 0 })
            .GroupBy(x => x.Date)
            .Select(g => new { Minutes = g.Sum(x => x.Minutes), Count = g.Count() })
            .ToListAsync(ct);
        return CatchUp.PaceOf(days.Select(d => (d.Minutes, d.Count)).ToList());
    }
}
