using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Places">Where the show is set, in Wikidata's order; each has no shows listed.</param>
/// <param name="Filmed">Where it was filmed, the same way.</param>
/// <param name="StandsIn">Filming places far from every place it's set in (<see cref="AtlasApi.StandInKilometres"/>), each
/// with the setting it plays.</param>
public record ShowSettingsDto(Guid ShowId, IReadOnlyList<AtlasPlaceDto> Places, IReadOnlyList<AtlasPlaceDto> Filmed,
    IReadOnlyList<StandInDto> StandsIn);

/// <param name="Plays">The nearest place the show is set in, which this filming place stands in for.</param>
public record StandInDto(string PlaceId, string Name, string? Country, string Plays);

/// <param name="ShowIds">The shows to map: the user's. Empty or missing: the catalog's most popular shows.</param>
/// <param name="Kind">"set" (the default) maps where shows are set, "filmed" where they were filmed.</param>
public record AtlasRequest(IReadOnlyList<Guid>? ShowIds, string? Kind = null);

/// <param name="Yours">Mapped from the shows the caller sent, not the popular fallback.</param>
/// <param name="Shows">Shows asked about; <paramref name="Placed"/> of them have a setting on the map.</param>
/// <param name="Places">Most shows first.</param>
/// <param name="Countries">Most shows first.</param>
/// <param name="StandIns">Filming places that play somewhere else, most shows first, whichever kind is mapped.</param>
public record AtlasDto(bool Yours, string Kind, int Shows, int Placed, IReadOnlyList<AtlasPlaceDto> Places,
    IReadOnlyList<AtlasCountryDto> Countries, IReadOnlyList<StandInCityDto> StandIns);

/// <param name="Shows">Each show filmed there and the place it's set in that this one plays.</param>
public record StandInCityDto(string PlaceId, string Name, string? Country, IReadOnlyList<StandInShowDto> Shows);

public record StandInShowDto(Guid ShowId, string Title, string? PosterUrl, string Plays);

/// <param name="PlaceId">Wikidata's item for the place ("Q34804").</param>
public record AtlasPlaceDto(string PlaceId, string Name, string? Country, double Latitude, double Longitude, IReadOnlyList<AtlasShowDto> Shows);

public record AtlasShowDto(Guid ShowId, string Title, string? PosterUrl);

public record AtlasCountryDto(string Name, int Shows, int Places);

/// <summary>
/// The TV atlas: where shows are set and filmed, from Wikidata's narrative and filming locations (the sync worker's
/// <c>SettingsService</c>).
/// The UI sends the user's shows, as Holiday episodes and Who said it? do, so the catalog never stores who follows what;
/// with none, the atlas maps the catalog's <see cref="Popular"/> most popular shows (cached an hour). A show's own
/// settings are cached a minute, like its detail.
/// <para>
/// <b>Stand-ins:</b> a filming place more than <see cref="StandInKilometres"/> from every place the show is set in plays
/// somewhere else (Stranger Things' Indiana, filmed around Atlanta). Places that are a whole country are left out of the
/// comparison on both sides, since a country's point says nothing about where in it a show is (Game of Thrones lists
/// ten filming countries).
/// </para>
/// </summary>
public static class AtlasApi
{
    public const int Popular = 150;
    public const double StandInKilometres = 300;
    private const int MaxShows = 1000;
    private static readonly TimeSpan PopularFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan ShowFor = TimeSpan.FromMinutes(1);

    public static void MapAtlasApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/settings", GetShowSettingsAsync).RequireFeature(FeatureFlags.TvAtlas);
        api.MapPost("/atlas", GetAtlasAsync).RequireFeature(FeatureFlags.TvAtlas);
    }

    private sealed record Row(string Kind, string PlaceId, string Name, string? Country, double Latitude, double Longitude);

    private static async Task<Results<Ok<ShowSettingsDto>, NotFound>> GetShowSettingsAsync(Guid id, CatalogContext db, HybridCache cache,
        CancellationToken ct)
    {
        var rows = await cache.GetOrCreateAsync($"settings:{id}", async token =>
                await db.Settings.AsNoTracking().Where(s => s.ShowId == id)
                    .Select(s => new Row(s.Kind, s.PlaceId, s.Name, s.Country, s.Latitude, s.Longitude))
                    .ToListAsync(token),
            new HybridCacheEntryOptions { Expiration = ShowFor, LocalCacheExpiration = ShowFor }, cancellationToken: ct);
        if (rows.Count == 0)
        {
            return TypedResults.NotFound();
        }
        static AtlasPlaceDto Place(Row r) => new(r.PlaceId, r.Name, r.Country, r.Latitude, r.Longitude, []);
        var set = rows.Where(r => r.Kind == ShowSettingKinds.Set).ToList();
        var filmed = rows.Where(r => r.Kind == ShowSettingKinds.Filmed).ToList();
        return TypedResults.Ok(new ShowSettingsDto(id, set.Select(Place).ToList(), filmed.Select(Place).ToList(),
            StandIns(set, filmed).Select(x => new StandInDto(x.Filmed.PlaceId, x.Filmed.Name, x.Filmed.Country, x.Plays)).ToList()));
    }

    private static async Task<Results<Ok<AtlasDto>, BadRequest<string>>> GetAtlasAsync(AtlasRequest request, CatalogContext db, HybridCache cache,
        CancellationToken ct)
    {
        if (request.ShowIds is { Count: > MaxShows })
        {
            return TypedResults.BadRequest($"At most {MaxShows} shows.");
        }
        var kind = request.Kind == ShowSettingKinds.Filmed ? ShowSettingKinds.Filmed : ShowSettingKinds.Set;
        if (request.ShowIds is { Count: > 0 } ids)
        {
            return TypedResults.Ok(await BuildAsync(db, ids.Distinct().ToList(), kind, yours: true, ct));
        }

        var popular = await cache.GetOrCreateAsync($"atlas:popular:{kind}", async token =>
        {
            var top = await db.Shows.AsNoTracking()
                .Where(s => db.Settings.Any(x => x.ShowId == s.Id))
                .OrderByDescending(s => s.Popularity).ThenByDescending(s => s.ExternalRating)
                .Select(s => s.Id)
                .Take(Popular)
                .ToListAsync(token);
            return await BuildAsync(db, top, kind, yours: false, token);
        }, new HybridCacheEntryOptions { Expiration = PopularFor, LocalCacheExpiration = PopularFor }, cancellationToken: ct);
        return TypedResults.Ok(popular);
    }

    private static async Task<AtlasDto> BuildAsync(CatalogContext db, List<Guid> showIds, string kind, bool yours, CancellationToken ct)
    {
        var all = await db.Settings.AsNoTracking().Where(s => showIds.Contains(s.ShowId))
            .Join(db.Shows, s => s.ShowId, show => show.Id, (s, show) => new
            {
                s.Kind, s.PlaceId, s.Name, s.Country, s.Latitude, s.Longitude, show.Id, show.Title, show.PosterUrl, show.Popularity
            })
            .ToListAsync(ct);
        var rows = all.Where(r => r.Kind == kind).ToList();

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

        var standIns = all.GroupBy(r => r.Id)
            .SelectMany(show => StandIns(
                    show.Where(r => r.Kind == ShowSettingKinds.Set).Select(r => new Row(r.Kind, r.PlaceId, r.Name, r.Country, r.Latitude, r.Longitude)).ToList(),
                    show.Where(r => r.Kind == ShowSettingKinds.Filmed).Select(r => new Row(r.Kind, r.PlaceId, r.Name, r.Country, r.Latitude, r.Longitude)).ToList())
                .Select(x => (x.Filmed, x.Plays, Show: show.First())))
            .GroupBy(x => x.Filmed.PlaceId)
            .Select(g => new StandInCityDto(g.Key, g.First().Filmed.Name, g.First().Filmed.Country,
                g.OrderByDescending(x => x.Show.Popularity).DistinctBy(x => x.Show.Id)
                    .Select(x => new StandInShowDto(x.Show.Id, x.Show.Title, x.Show.PosterUrl, x.Plays)).ToList()))
            .OrderByDescending(c => c.Shows.Count).ThenBy(c => c.Name)
            .ToList();
        return new AtlasDto(yours, kind, showIds.Count, rows.Select(r => r.Id).Distinct().Count(), places, countries, standIns);
    }

    /// <summary>The show's filming places far from every place it's set in, each with the nearest setting it plays.</summary>
    private static IEnumerable<(Row Filmed, string Plays)> StandIns(List<Row> set, List<Row> filmed)
    {
        var settings = set.Where(r => !IsCountry(r)).ToList();
        if (settings.Count == 0)
        {
            yield break;
        }
        foreach (var place in filmed.Where(r => !IsCountry(r)))
        {
            var nearest = settings.MinBy(s => Kilometres(s, place))!;
            if (Kilometres(nearest, place) > StandInKilometres)
            {
                yield return (place, nearest.Name);
            }
        }
    }

    /// <summary>The place is a whole country: Wikidata gives a country as its own country.</summary>
    private static bool IsCountry(Row place) => place.Country == place.Name;

    /// <summary>Great-circle distance.</summary>
    private static double Kilometres(Row a, Row b)
    {
        static double Radians(double degrees) => degrees * Math.PI / 180;
        var dLat = Radians(b.Latitude - a.Latitude);
        var dLon = Radians(b.Longitude - a.Longitude);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Radians(a.Latitude)) * Math.Cos(Radians(b.Latitude)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * 6371 * Math.Asin(Math.Sqrt(h));
    }
}
