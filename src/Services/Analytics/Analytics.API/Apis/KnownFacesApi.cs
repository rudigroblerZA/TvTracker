using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Known faces (see <see cref="KnownFaces"/>), computed on request from the caller's own ratings and watches.
/// </summary>
public static class KnownFacesApi
{
    public static void MapKnownFacesApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/{showId:guid}/known-faces", GetAsync).RequireFeature(FeatureFlags.KnownFaces);

    private static async Task<Ok<KnownFacesDto>> GetAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var cast = await db.Cast.AsNoTracking()
            .Where(c => c.ShowId == showId && c.PersonId != null)
            .OrderBy(c => c.Order)
            .ToListAsync(ct);
        var personIds = cast.Select(c => c.PersonId!.Value).Distinct().ToList();

        var rated = await db.ShowRatings.AsNoTracking()
            .Where(r => r.UserId == userId && r.ShowId != showId)
            .Select(r => r.ShowId)
            .ToListAsync(ct);
        var watchedShows = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId && w.ShowId != showId)
            .Select(w => w.ShowId)
            .Distinct()
            .ToListAsync(ct);
        var seen = rated.Concat(watchedShows).Distinct().ToList();

        var elsewhere = (await db.Cast.AsNoTracking()
                .Where(c => c.PersonId != null && personIds.Contains(c.PersonId.Value) && seen.Contains(c.ShowId))
                .ToListAsync(ct))
            .ToLookup(c => c.PersonId!.Value);
        var fromIds = elsewhere.SelectMany(g => g).Select(c => c.ShowId).Distinct().ToList();

        var shows = await db.Shows.AsNoTracking().Where(s => fromIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        var progress = await Viewer.ProgressAsync(db, userId, time.GetUtcNow(), fromIds, ct);
        return TypedResults.Ok(new KnownFacesDto(showId, personIds.Count, KnownFaces.Of(cast, elsewhere, shows, progress, rated.ToHashSet())));
    }
}
