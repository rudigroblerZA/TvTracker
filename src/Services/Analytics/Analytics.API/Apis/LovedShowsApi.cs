using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="Weight">How much it counts as loved (<see cref="ReunionsApi.LoveWeight"/>): 1 for 5 stars, 0.8 for 4, 0.5
/// followed or watched and unrated.</param>
public record LovedShowDto(Guid ShowId, double Weight);

/// <param name="Known">Every show the caller rated, follows or watched, loved or not: what From the makers leaves out.</param>
public record LovedShowsDto(IReadOnlyList<LovedShowDto> Loved, IReadOnlyList<Guid> Known);

/// <summary>
/// The caller's loved and known shows, for From the makers: the page sends them to Catalog.API, which knows who made what,
/// so neither service needs the other's data and the catalog never stores them. The same rules as the reunion radar.
/// </summary>
public static class LovedShowsApi
{
    /// <summary>
    /// The most the catalog reads; a longer history is cut to the most loved.
    /// </summary>
    private const int MaxLoved = 300;

    public static void MapLovedShowsApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/loved", GetAsync).RequireFeature(FeatureFlags.MakersForYou);

    private static async Task<Ok<LovedShowsDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, CancellationToken ct)
    {
        var (known, loved) = await ReunionsApi.LoveAsync(db, user.GetUserId(), ct);
        return TypedResults.Ok(new LovedShowsDto(
            loved.OrderByDescending(l => l.Value).Take(MaxLoved).Select(l => new LovedShowDto(l.Key, l.Value)).ToList(),
            known.ToList()));
    }
}
