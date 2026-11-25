using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Taste twins (see <see cref="TasteTwins"/>): agreement is worked out in the database, the rest on request.
/// </summary>
public static class TasteTwinsApi
{
    public static void MapTasteTwinsApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/taste-twins", GetTwinsAsync).RequireFeature(FeatureFlags.TasteTwins);

    private static async Task<Ok<TasteTwinsDto>> GetTwinsAsync(
        ClaimsPrincipal user, AnalyticsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var mine = await db.ShowRatings.AsNoTracking().Where(r => r.UserId == userId).ToDictionaryAsync(r => r.ShowId, r => r.Stars, ct);
        if (mine.Count < TasteTwins.MinRated)
        {
            return TypedResults.Ok(TasteTwins.TooFewRated(mine.Count));
        }

        // Agreement with everyone who shares 3+ rated shows, worked out in the database.
        var candidates = await (
                from other in db.ShowRatings
                join own in db.ShowRatings.Where(r => r.UserId == userId) on other.ShowId equals own.ShowId
                where other.UserId != userId
                group Math.Abs(other.Stars - own.Stars) by other.UserId into g
                where g.Count() >= TasteTwins.MinShared
                select new { UserId = g.Key, Shared = g.Count(), Difference = g.Average(d => (double)d) })
            .ToListAsync(ct);
        var twins = TasteTwins.Twins(candidates.Select(c => (c.UserId, c.Shared, c.Difference)));
        if (twins.Count < InsightRules.MinUsersToShow)
        {
            return TypedResults.Ok(TasteTwins.TooFewTwins(mine.Count));
        }

        var twinIds = twins.Select(t => t.UserId).ToList();
        var theirs = await db.ShowRatings.AsNoTracking()
            .Where(r => twinIds.Contains(r.UserId) && r.Stars >= TasteTwins.LovedStars)
            .Select(r => new TasteTwins.Loved(r.UserId, r.ShowId, r.Stars))
            .ToListAsync(ct);

        var followed = await db.Follows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.ShowId).ToListAsync(ct);
        var watched = await db.Watches.AsNoTracking().Where(w => w.UserId == userId).Select(w => w.ShowId).Distinct().ToListAsync(ct);
        var (picks, agreeOn) = TasteTwins.Choose(theirs, twins, mine, mine.Keys.Concat(followed).Concat(watched).ToHashSet());

        var ids = picks.Select(p => p.ShowId).Concat(agreeOn).ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        return TypedResults.Ok(TasteTwins.Of(mine.Count, twins, picks, agreeOn, shows));
    }
}
