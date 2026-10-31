using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Places">Where the show is set, in Wikidata's order; each has no shows listed.</param>
public record ShowSettingsDto(Guid ShowId, IReadOnlyList<AtlasPlaceDto> Places);

/// <param name="ShowIds">The shows to map: the user's. Empty or missing: the catalog's most popular shows.</param>
public record AtlasRequest(IReadOnlyList<Guid>? ShowIds);

/// <param name="Yours">Mapped from the shows the caller sent, not the popular fallback.</param>
/// <param name="Shows">Shows asked about; <paramref name="Placed"/> of them have a setting on the map.</param>
/// <param name="Places">Most shows first.</param>
/// <param name="Countries">Most shows first.</param>
public record AtlasDto(bool Yours, int Shows, int Placed, IReadOnlyList<AtlasPlaceDto> Places, IReadOnlyList<AtlasCountryDto> Countries);

/// <param name="PlaceId">Wikidata's item for the place ("Q34804").</param>
public record AtlasPlaceDto(string PlaceId, string Name, string? Country, double Latitude, double Longitude, IReadOnlyList<AtlasShowDto> Shows);

public record AtlasShowDto(Guid ShowId, string Title, string? PosterUrl);

public record AtlasCountryDto(string Name, int Shows, int Places);

/// <summary>
/// The TV atlas: where shows are set, from Wikidata's narrative locations (the sync worker's <c>SettingsService</c>).
/// The UI sends the user's shows, as Holiday episodes and Who said it? do, so the catalog never stores who follows what;
/// with none, the atlas maps the catalog's <see cref="Popular"/> most popular shows (cached an hour). A show's own
/// settings are cached a minute, like its detail.
/// </summary>
public static class AtlasApi
{
    public const int Popular = 150;
    private const int MaxShows = 1000;
    private static readonly TimeSpan PopularFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan ShowFor = TimeSpan.FromMinutes(1);

    public static void MapAtlasApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/settings", GetShowSettingsAsync).RequireFeature(FeatureFlags.TvAtlas);
        api.MapPost("/atlas", GetAtlasAsync).RequireFeature(FeatureFlags.TvAtlas);
    }

    private static async Task<Results<Ok<ShowSettingsDto>, NotFound>> GetShowSettingsAsync(Guid id, CatalogContext db, HybridCache cache,
        CancellationToken ct)
    {
        var places = await cache.GetOrCreateAsync($"settings:{id}", async token =>
                await db.Settings.AsNoTracking().Where(s => s.ShowId == id)
                    .Select(s => new AtlasPlaceDto(s.PlaceId, s.Name, s.Country, s.Latitude, s.Longitude, new List<AtlasShowDto>()))
                    .ToListAsync(token),
            new HybridCacheEntryOptions { Expiration = ShowFor, LocalCacheExpiration = ShowFor }, cancellationToken: ct);
        return places.Count == 0 ? TypedResults.NotFound() : TypedResults.Ok(new ShowSettingsDto(id, places));
    }

    private static async Task<Results<Ok<AtlasDto>, BadRequest<string>>> GetAtlasAsync(AtlasRequest request, CatalogContext db, HybridCache cache,
        CancellationToken ct)
    {
        if (request.ShowIds is { Count: > MaxShows })
        {
            return TypedResults.BadRequest($"At most {MaxShows} shows.");
        }
        if (request.ShowIds is { Count: > 0 } ids)
        {
            return TypedResults.Ok(await BuildAsync(db, ids.Distinct().ToList(), yours: true, ct));
        }

        var popular = await cache.GetOrCreateAsync("atlas:popular", async token =>
        {
            var top = await db.Shows.AsNoTracking()
                .Where(s => db.Settings.Any(x => x.ShowId == s.Id))
                .OrderByDescending(s => s.Popularity).ThenByDescending(s => s.ExternalRating)
                .Select(s => s.Id)
                .Take(Popular)
                .ToListAsync(token);
            return await BuildAsync(db, top, yours: false, token);
        }, new HybridCacheEntryOptions { Expiration = PopularFor, LocalCacheExpiration = PopularFor }, cancellationToken: ct);
        return TypedResults.Ok(popular);
    }

    private static async Task<AtlasDto> BuildAsync(CatalogContext db, List<Guid> showIds, bool yours, CancellationToken ct)
    {
        var rows = await db.Settings.AsNoTracking().Where(s => showIds.Contains(s.ShowId))
            .Join(db.Shows, s => s.ShowId, show => show.Id, (s, show) => new
            {
                s.PlaceId, s.Name, s.Country, s.Latitude, s.Longitude, show.Id, show.Title, show.PosterUrl, show.Popularity
            })
            .ToListAsync(ct);

        var places = rows.GroupBy(r => r.PlaceId)
            .Select(g => new AtlasPlaceDto(g.Key, g.First().Name, g.First().Country, g.First().Latitude, g.First().Longitude,
                g.OrderByDescending(r => r.Popularity).Select(r => new AtlasShowDto(r.Id, r.Title, r.PosterUrl)).ToList()))
            .OrderByDescending(p => p.Shows.Count).ThenBy(p => p.Name)
            .ToList();
        var countries = rows.Where(r => r.Country is not null)
            .GroupBy(r => r.Country!)
            .Select(g => new AtlasCountryDto(g.Key, g.Select(r => r.Id).Distinct().Count(), g.Select(r => r.PlaceId).Distinct().Count()))
            .OrderByDescending(c => c.Shows).ThenBy(c => c.Name)
            .ToList();
        return new AtlasDto(yours, showIds.Count, rows.Select(r => r.Id).Distinct().Count(), places, countries);
    }
}
