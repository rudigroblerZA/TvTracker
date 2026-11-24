using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <summary>
/// Reunion radar (see <see cref="Reunions"/>), from the user's own ratings, follows and watches and their loved shows' casts.
/// </summary>
public static class ReunionsApi
{
    public static void MapReunionsApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/reunions", GetRadarAsync).RequireFeature(FeatureFlags.ReunionRadar);

    private static async Task<Ok<ReunionRadarDto>> GetRadarAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var (known, loved) = await LoveAsync(db, user.GetUserId(), ct);
        var lovedIds = loved.Keys.ToList();

        var shows = await db.Shows.AsNoTracking().Where(s => lovedIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        var cast = (await db.Cast.AsNoTracking().Where(c => lovedIds.Contains(c.ShowId) && c.PersonId != null).ToListAsync(ct))
            .Where(c => !Reunions.IsSelfRole(c.Character, c.PersonName))
            .ToList();
        var personIds = cast.Select(c => c.PersonId!.Value).Distinct().ToList();
        var credits = (await db.CastCredits.AsNoTracking().Where(c => personIds.Contains(c.PersonId)).ToListAsync(ct))
            .ToLookup(c => c.PersonId);

        var best = Reunions.Find(loved, known, shows, cast, credits, time.GetUtcNow());
        var pending = personIds.Count(id => !credits[id].Any());
        return TypedResults.Ok(new ReunionRadarDto(shows.Count, pending, best));
    }

    /// <summary>
    /// The shows the user knows (rated, followed or watched) and, among them, the ones they love with their weights
    /// (<see cref="Reunions.LoveWeight"/>). Shared with From the makers, so "loved" means the same in both.
    /// </summary>
    internal static async Task<(HashSet<Guid> Known, Dictionary<Guid, double> Loved)> LoveAsync(AnalyticsContext db, string userId,
        CancellationToken ct)
    {
        var stars = await db.ShowRatings.AsNoTracking().Where(r => r.UserId == userId).ToDictionaryAsync(r => r.ShowId, r => r.Stars, ct);
        var followed = await db.Follows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.ShowId).ToListAsync(ct);
        var watched = await db.Watches.AsNoTracking().Where(w => w.UserId == userId)
            .GroupBy(w => w.ShowId).Select(g => new { ShowId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(w => w.ShowId, w => w.Count, ct);

        var known = stars.Keys.Concat(followed).Concat(watched.Keys).ToHashSet();
        var loved = known
            .Select(id => (ShowId: id, Weight: Reunions.LoveWeight(stars.GetValueOrDefault(id), followed.Contains(id), watched.GetValueOrDefault(id))))
            .Where(x => x.Weight > 0)
            .ToDictionary(x => x.ShowId, x => x.Weight);
        return (known, loved);
    }

    // Character matching, by the same rules as Catalog.API's Universes (which links shows by shared characters).
}
