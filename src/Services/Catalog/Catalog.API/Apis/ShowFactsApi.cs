using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="AiredEpisodes">Regular episodes aired so far (specials left out).</param>
public record ShowFactsDto(Guid Id, string Title, string? PosterUrl, string Source, string ExternalId, string? Network, DateOnly? FirstAirDate,
    ShowStatus Status, double Popularity, int AiredEpisodes);

/// <summary>
/// The catalog's facts about its most popular shows, as the catalog holds them now (overrides applied), for Research's
/// queue: which popular shows have no research, and where Wikidata disagrees (network, premiere date, episode count).
/// TVmaze's popularity tops out (a fifth of the catalog at 99 or 100), so the provider's rating breaks ties rather than
/// the alphabet.
/// </summary>
public static class ShowFactsApi
{
    private const int MaxLimit = 1000;

    public static void MapShowFactsApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/facts", GetFactsAsync).RequireFeature(FeatureFlags.Research);

    private static async Task<Ok<List<ShowFactsDto>>> GetFactsAsync(CatalogContext db, TimeProvider time, int limit = 300, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var shows = await db.Shows.AsNoTracking()
            .OrderByDescending(s => s.Popularity).ThenByDescending(s => s.ExternalRating).ThenBy(s => s.Title)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(s => new
            {
                s.Id, s.Title, s.PosterUrl, s.Source, s.ExternalId, s.Network, s.FirstAirDate, s.Status, s.Popularity,
                Aired = db.Episodes.Count(e => e.ShowId == s.Id && e.SeasonNumber > 0 && e.AirsAtUtc != null && e.AirsAtUtc <= now)
            })
            .ToListAsync(ct);
        return TypedResults.Ok(shows.Select(s => new ShowFactsDto(s.Id, s.Title, s.PosterUrl, s.Source, s.ExternalId, s.Network,
            s.FirstAirDate, s.Status, s.Popularity, s.Aired)).ToList());
    }
}
