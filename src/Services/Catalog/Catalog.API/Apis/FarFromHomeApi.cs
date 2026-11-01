using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="SetIn">The one country the show is set in (Wikidata, as the TV atlas has it).</param>
/// <param name="Known">Main cast members whose birthplace TVmaze knows.</param>
/// <param name="Elsewhere">Of those, the ones born outside <paramref name="SetIn"/>, in billing order.</param>
public record ShowCastOriginsDto(string SetIn, int Known, IReadOnlyList<BornElsewhereDto> Elsewhere);

public record BornElsewhereDto(Guid PersonId, string Name, string? PhotoUrl, string? Character, string BornIn, bool Lead);

/// <param name="Leads">Popular shows with a lead born outside the country the show is set in, most popular first.</param>
/// <param name="Countries">For each country with enough shows set there, where their main casts were born.</param>
public record FarFromHomeDto(IReadOnlyList<FarFromHomeShowDto> Leads, IReadOnlyList<SettingCountryDto> Countries);

public record FarFromHomeShowDto(Guid ShowId, string Title, string? PosterUrl, string SetIn, IReadOnlyList<BornElsewhereDto> Leads);

/// <param name="Shows">Shows set there with a known cast.</param>
/// <param name="Cast">Main cast members of those shows with a known birthplace, each actor once.</param>
/// <param name="Home">How many of them were born there.</param>
/// <param name="BornIn">The other birthplaces, most first.</param>
public record SettingCountryDto(string Country, int Shows, int Cast, int Home, IReadOnlyList<BirthplaceCountDto> BornIn);

public record BirthplaceCountDto(string Country, int Count);

/// <summary>
/// Far from home: actors in the main cast of a show set in one country who were born in another, from the TV atlas's
/// settings (Wikidata's narrative location, <c>show_settings</c>) and TVmaze's birthplaces (<c>people.Country</c>, about
/// 86% of catalog actors). Martin Freeman (United Kingdom) in Fargo, Charlie Cox (United Kingdom) in Daredevil.
/// <list type="bullet">
/// <item><b>Born, not from:</b> TVmaze gives a birthplace, not a nationality or upbringing (Glenn Howerton was born in
/// Japan and raised in Florida), so everything says "born in".</item>
/// <item><b>One country only:</b> shows set in several countries (Wikidata lists Killing Eve's many) are left out, as are
/// shows with no setting; there's nothing to be far from.</item>
/// <item><b>Scripted shows only:</b> an animated show's cast are voices, and reality casts play themselves (self roles
/// are left out too).</item>
/// <item><b>Names compared, two sources:</b> Wikidata says "South Korea" where TVmaze says "Korea, Republic of", so a
/// short table maps TVmaze's ISO-style names to Wikidata's; US and British territories that aren't another country
/// (Puerto Rico, the US Virgin Islands, the Isle of Man) count as their country.</item>
/// </list>
/// Lists: shows of popularity <see cref="ListPopularity"/>+ with a lead (the first <see cref="Leads"/> billed) born
/// elsewhere, and per setting country with <see cref="MinShows"/>+ shows, where their casts were born. Built together and
/// cached <see cref="CacheFor"/>.
/// </summary>
public static class FarFromHomeApi
{
    public const int Leads = 3;
    private const double ListPopularity = 90;
    private const int MinShows = 10;
    private const int MaxRows = 40;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    /// <summary>TVmaze's country names where Wikidata's differ.</summary>
    private static readonly Dictionary<string, string> Same = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Korea, Republic of"] = "South Korea",
        ["Korea, Democratic People's Republic of"] = "North Korea",
        ["Russian Federation"] = "Russia",
        ["Iran, Islamic Republic of"] = "Iran",
        ["Viet Nam"] = "Vietnam",
        ["Taiwan, Province of China"] = "Taiwan",
        ["Venezuela, Bolivarian Republic of"] = "Venezuela",
        ["Moldova, Republic of"] = "Moldova",
        ["Tanzania, United Republic of"] = "Tanzania",
        ["China"] = "People's Republic of China",
        ["Bahamas"] = "The Bahamas",
        ["Czechia"] = "Czech Republic",
    };

    /// <summary>
    /// Territories that aren't another country for this purpose: born in Puerto Rico is born in the United States. Used
    /// only to decide "elsewhere"; the birthplace is still shown as TVmaze gives it (Joe Locke, born in the Isle of Man).
    /// </summary>
    private static readonly Dictionary<string, string> Territories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Puerto Rico"] = "United States",
        ["Virgin Islands, U.S."] = "United States",
        ["Guam"] = "United States",
        ["Isle of Man"] = "United Kingdom",
        ["Jersey"] = "United Kingdom",
        ["Guernsey"] = "United Kingdom",
    };

    /// <summary>Wikidata's names shown as people say them.</summary>
    private static readonly Dictionary<string, string> Shown = new() { ["People's Republic of China"] = "China" };

    private sealed record CastRow(Guid ShowId, Guid PersonId, string Name, string? PhotoUrl, string? Character, int Order, string? Country);

    private sealed record ShowRow(Guid Id, string Title, string? PosterUrl, double Popularity);

    private sealed record Index(Dictionary<Guid, ShowCastOriginsDto> ByShow, FarFromHomeDto Lists);

    public static void MapFarFromHomeApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/cast-origins", GetForShowAsync).RequireFeature(FeatureFlags.FarFromHome);
        api.MapGet("/far-from-home", GetListsAsync).RequireFeature(FeatureFlags.FarFromHome);
    }

    private static async Task<Results<Ok<ShowCastOriginsDto>, NoContent>> GetForShowAsync(Guid id, CatalogContext db, HybridCache cache,
        CancellationToken ct) =>
        (await LoadAsync(db, cache, ct)).ByShow.TryGetValue(id, out var found) ? TypedResults.Ok(found) : TypedResults.NoContent();

    private static async Task<Ok<FarFromHomeDto>> GetListsAsync(CatalogContext db, HybridCache cache, CancellationToken ct) =>
        TypedResults.Ok((await LoadAsync(db, cache, ct)).Lists);

    /// <summary>A birthplace as Wikidata names countries.</summary>
    internal static string Country(string name) => Same.GetValueOrDefault(name, name);

    /// <summary>The country a birthplace counts as, for comparing with a setting.</summary>
    private static string CountsAs(string name) => Territories.GetValueOrDefault(Country(name), Country(name));

    private static string Show(string country) => Shown.GetValueOrDefault(country, country);

    private static async Task<Index> LoadAsync(CatalogContext db, HybridCache cache, CancellationToken ct) =>
        await cache.GetOrCreateAsync("far-from-home", async token =>
        {
            // Shows set in exactly one country.
            var settings = await db.Settings.AsNoTracking()
                .Where(s => s.Kind == ShowSettingKinds.Set && s.Country != null)
                .Select(s => new { s.ShowId, Country = s.Country! })
                .ToListAsync(token);
            var setIn = settings.GroupBy(s => s.ShowId)
                .Where(g => g.Select(s => s.Country).Distinct().Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().Country);
            var ids = setIn.Keys.ToList();
            var shows = await db.Shows.AsNoTracking()
                .Where(s => ids.Contains(s.Id) && s.Keywords.Contains("scripted"))
                .Select(s => new ShowRow(s.Id, s.Title, s.PosterUrl, s.Popularity))
                .ToDictionaryAsync(s => s.Id, token);
            var scripted = shows.Keys.ToList();
            var cast = await db.Cast.AsNoTracking()
                .Where(c => scripted.Contains(c.ShowId))
                .Select(c => new CastRow(c.ShowId, c.PersonId, c.Person.Name, c.Person.PhotoUrl, c.Character, c.Order, c.Person.Country))
                .ToListAsync(token);

            var byShow = new Dictionary<Guid, ShowCastOriginsDto>();
            var known = new Dictionary<Guid, List<CastRow>>();
            foreach (var group in cast.GroupBy(c => c.ShowId))
            {
                // Billing order as listed; a self role isn't a part played, and an actor listed twice counts once.
                var members = group.Where(c => c.Character is null || !Universes.IsSelfRole(c.Character, c.Name))
                    .OrderBy(c => c.Order).DistinctBy(c => c.PersonId).ToList();
                var withCountry = members.Where(c => !string.IsNullOrEmpty(c.Country)).ToList();
                if (withCountry.Count == 0)
                {
                    continue;
                }
                var home = setIn[group.Key];
                var leads = members.Take(Leads).Select(c => c.PersonId).ToHashSet();
                known[group.Key] = withCountry;
                byShow[group.Key] = new ShowCastOriginsDto(Show(home), withCountry.Count, withCountry
                    .Where(c => CountsAs(c.Country!) != home)
                    .Select(c => new BornElsewhereDto(c.PersonId, c.Name, c.PhotoUrl, c.Character, Show(Country(c.Country!)), leads.Contains(c.PersonId)))
                    .ToList());
            }

            var leadList = byShow
                .Where(s => shows[s.Key].Popularity >= ListPopularity && s.Value.Elsewhere.Any(e => e.Lead))
                .OrderByDescending(s => shows[s.Key].Popularity).ThenBy(s => shows[s.Key].Title, StringComparer.Ordinal)
                .Take(MaxRows)
                .Select(s => new FarFromHomeShowDto(s.Key, shows[s.Key].Title, shows[s.Key].PosterUrl, s.Value.SetIn,
                    s.Value.Elsewhere.Where(e => e.Lead).ToList()))
                .ToList();

            // Per setting country: each actor once (the same actor in five Chicago shows is one person).
            var countries = known
                .GroupBy(k => setIn[k.Key])
                .Where(g => g.Count() >= MinShows)
                .Select(g =>
                {
                    var people = g.SelectMany(k => k.Value).DistinctBy(c => c.PersonId).ToList();
                    var births = people.Where(c => CountsAs(c.Country!) != g.Key).GroupBy(c => Country(c.Country!))
                        .ToDictionary(b => b.Key, b => b.Count());
                    return new SettingCountryDto(Show(g.Key), g.Count(), people.Count, people.Count - births.Values.Sum(),
                        births.OrderByDescending(b => b.Value).ThenBy(b => b.Key, StringComparer.Ordinal)
                            .Take(8).Select(b => new BirthplaceCountDto(Show(b.Key), b.Value)).ToList());
                })
                .OrderByDescending(c => c.Shows)
                .ToList();
            return new Index(byShow, new FarFromHomeDto(leadList, countries));
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);
}
