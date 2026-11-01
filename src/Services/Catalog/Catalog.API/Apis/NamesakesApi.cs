using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="Country">The show's own network country code, when known.</param>
/// <param name="Namesakes">Other shows with the same title, earliest first.</param>
/// <param name="Checked">Whether the show has been looked up yet; until then, no namesakes means "not known yet".</param>
public record ShowNamesakesDto(string? Country, IReadOnlyList<NamesakeDto> Namesakes, bool Checked);

/// <param name="ShowId">The namesake's catalog id when it's in the catalog too, else null (the page offers a search, which imports it).</param>
/// <param name="Kind">The provider's type ("scripted", "reality", …).</param>
/// <param name="SharedCast">Actors of the catalog show credited in the namesake as well, billing order: a comeback rather than a namesake.</param>
public record NamesakeDto(string ExternalId, Guid? ShowId, string Title, string? PosterUrl, int? Year, int? EndYear, string? Network, string? Country,
    string? Language, string? Kind, double? Rating, IReadOnlyList<SharedActorDto> SharedCast);

public record SharedActorDto(Guid PersonId, string Name, string? Character);

/// <param name="Back">Same title and some of the same cast: revivals and continuations.</param>
/// <param name="Abroad">Same title and kind, made in another country, with none of the same cast.</param>
/// <param name="YearsApart">Same title and kind, not abroad, <see cref="NamesakesApi.YearsApart"/>+ years apart, none of the same cast.</param>
public record NamesakeListsDto(IReadOnlyList<NamesakePairDto> Back, IReadOnlyList<NamesakePairDto> Abroad, IReadOnlyList<NamesakePairDto> YearsApart);

/// <param name="Show">The catalog show the pair is listed under: the later of the two when both are in the catalog.</param>
public record NamesakePairDto(NamesakeShowDto Show, NamesakeDto Other);

public record NamesakeShowDto(Guid Id, string Title, string? PosterUrl, int? Year, string? Country);

/// <summary>
/// Same name: other shows with a catalog show's title, found by the sync worker's monthly title search
/// (<c>show_namesakes</c>). Facts only, since a title alone can't tell a remake from a namesake (Friends of 1979 is
/// nothing to do with Friends); what can be said is said:
/// <list type="bullet">
/// <item><b>Shared cast</b> from the provider's credits (<c>person_cast_credits</c>) of the catalog show's actors: Kelsey
/// Grammer in both Frasiers makes it a comeback. Self roles don't count, as everywhere.</item>
/// <item><b>Another country</b>, from the networks' countries (or languages, when TVmaze gives no country): the usual shape of a remake (The Office, Shameless,
/// House of Cards), still not claimed as one.</item>
/// </list>
/// The lists are scripted and animated catalog shows of popularity <see cref="ListPopularity"/>+ whose namesake is the
/// same kind, each pair once. Everything is built together and cached <see cref="CacheFor"/>.
/// </summary>
public static class NamesakesApi
{
    public const int YearsApart = 10;
    private const double ListPopularity = 90;
    private const int MaxRows = 30;
    // An hour, not six: the sync worker looks a few shows up a minute, so a new catalog fills in over the first hour.
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private sealed record ShowRow(Guid Id, string Title, string? PosterUrl, string ExternalId, DateOnly? FirstAirDate, double Popularity, List<string> Keywords);

    private sealed record CastRow(Guid ShowId, Guid PersonId, string Name, string? Character, int Order);

    private sealed record CreditRow(Guid PersonId, string ShowExternalId, string? Character);

    /// <summary>Everything, keyed by catalog show, plus the show rows the lists need.</summary>
    private sealed record Index(Dictionary<Guid, ShowNamesakesDto> ByShow, List<IndexedShow> Shows);

    private sealed record IndexedShow(Guid Id, string Title, string? PosterUrl, int? Year, string ExternalId, double Popularity, string? Kind,
        List<string> Keywords);

    public static void MapNamesakesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/namesakes", GetForShowAsync).RequireFeature(FeatureFlags.Namesakes);
        api.MapGet("/namesakes", GetListsAsync).RequireFeature(FeatureFlags.Namesakes);
    }

    private static async Task<Results<Ok<ShowNamesakesDto>, NotFound>> GetForShowAsync(Guid id, CatalogContext db, HybridCache cache, CancellationToken ct)
    {
        var index = await LoadAsync(db, cache, ct);
        if (index.ByShow.TryGetValue(id, out var found))
        {
            return TypedResults.Ok(found);
        }
        return await db.Shows.AnyAsync(s => s.Id == id, ct) ? TypedResults.Ok(new ShowNamesakesDto(null, [], false)) : TypedResults.NotFound();
    }

    private static async Task<Ok<NamesakeListsDto>> GetListsAsync(CatalogContext db, HybridCache cache, CancellationToken ct)
    {
        var index = await LoadAsync(db, cache, ct);
        var pairs = index.Shows
            .Where(s => s.Popularity >= ListPopularity && s.Kind is { } kind && ShowRules.StoryKinds.Contains(kind))
            .OrderByDescending(s => s.Popularity).ThenBy(s => s.Title, StringComparer.Ordinal)
            .SelectMany(s =>
            {
                var own = index.ByShow[s.Id];
                return own.Namesakes
                    .Where(n => n.Kind == s.Kind && n.Year is not null)
                    .Select(n => (Show: new NamesakeShowDto(s.Id, s.Title, s.PosterUrl, s.Year, own.Country), Other: n, Ext: s.ExternalId,
                        Abroad: Abroad(own.Country, s.Keywords, n)));
            })
            .ToList();

        // Each pair once: under the later show when both are in the catalog (Frasier 2023, bringing back Frasier 1993).
        var listed = new HashSet<(string, string)>();
        var unique = new List<(NamesakePairDto Pair, bool Abroad)>();
        foreach (var p in pairs.OrderBy(p => p.Other.ShowId is not null && p.Other.Year > p.Show.Year ? 1 : 0))
        {
            var key = string.CompareOrdinal(p.Ext, p.Other.ExternalId) < 0 ? (p.Ext, p.Other.ExternalId) : (p.Other.ExternalId, p.Ext);
            if (listed.Add(key))
            {
                unique.Add((new NamesakePairDto(p.Show, p.Other), p.Abroad));
            }
        }
        // Back in the original order (most popular show first), now that the duplicates are gone.
        var order = pairs.Select(p => p.Show.Id).Distinct().Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var ordered = unique.OrderBy(p => order[p.Pair.Show.Id]).ThenBy(p => p.Pair.Other.Year).ToList();

        static int Gap(NamesakePairDto p) => Math.Abs(p.Other.Year!.Value - (p.Show.Year ?? p.Other.Year.Value));
        return TypedResults.Ok(new NamesakeListsDto(
            ordered.Where(p => p.Pair.Other.SharedCast.Count > 0).Select(p => p.Pair).Take(MaxRows).ToList(),
            ordered.Where(p => p.Pair.Other.SharedCast.Count == 0 && p.Abroad).Select(p => p.Pair).Take(MaxRows).ToList(),
            ordered.Where(p => p.Pair.Other.SharedCast.Count == 0 && !p.Abroad && Gap(p.Pair) >= YearsApart).Select(p => p.Pair).Take(MaxRows).ToList()));
    }

    /// <summary>
    /// Made in another country: the networks' countries differ, or, when either is missing (TVmaze gives global
    /// streamers' shows none: Frasier on Paramount+), the languages do (the show's is among its keywords). By country
    /// alone, Criminal Minds' Korean namesake read as "the same country".
    /// </summary>
    private static bool Abroad(string? country, IReadOnlyCollection<string> keywords, NamesakeDto namesake) =>
        country is { } a && namesake.Country is { } b
            ? a != b
            : namesake.Language is { } language && !keywords.Contains(language.ToLowerInvariant());

    private static async Task<Index> LoadAsync(CatalogContext db, HybridCache cache, CancellationToken ct) =>
        await cache.GetOrCreateAsync("namesakes", async token =>
        {
            var namesakes = await db.Namesakes.AsNoTracking().ToListAsync(token);
            var lookups = await db.NamesakeLookups.AsNoTracking().ToDictionaryAsync(l => l.ShowId, l => l.Country, token);
            var shows = await db.Shows.AsNoTracking()
                .Where(s => db.NamesakeLookups.Any(l => l.ShowId == s.Id))
                .Select(s => new ShowRow(s.Id, s.Title, s.PosterUrl, s.ExternalId, s.FirstAirDate, s.Popularity, s.Keywords))
                .ToListAsync(token);
            var catalogByExternal = await db.Shows.AsNoTracking().Select(s => new { s.ExternalId, s.Id }).ToDictionaryAsync(s => s.ExternalId, s => s.Id, token);

            var withNamesakes = namesakes.Select(n => n.ShowId).Distinct().ToList();
            var cast = await db.Cast.AsNoTracking()
                .Where(c => withNamesakes.Contains(c.ShowId))
                .Select(c => new CastRow(c.ShowId, c.PersonId, c.Person.Name, c.Character, c.Order))
                .ToListAsync(token);
            var people = cast.Select(c => c.PersonId).Distinct().ToList();
            var others = namesakes.Select(n => n.ExternalId).Distinct().ToList();
            var credits = await db.CastCredits.AsNoTracking()
                .Where(c => people.Contains(c.PersonId) && others.Contains(c.ShowExternalId))
                .Select(c => new CreditRow(c.PersonId, c.ShowExternalId, c.Character))
                .ToListAsync(token);
            var credited = credits.ToLookup(c => (c.PersonId, c.ShowExternalId));
            var castByShow = cast.Where(c => c.Character is null || !Universes.IsSelfRole(c.Character, c.Name)).ToLookup(c => c.ShowId);

            var byShow = new Dictionary<Guid, ShowNamesakesDto>();
            var namesakesByShow = namesakes.ToLookup(n => n.ShowId);
            foreach (var show in shows)
            {
                var list = namesakesByShow[show.Id]
                    .Select(n =>
                    {
                        var shared = castByShow[show.Id]
                            .Where(c => credited[(c.PersonId, n.ExternalId)].Any(r => r.Character is null || !Universes.IsSelfRole(r.Character, c.Name)))
                            .OrderBy(c => c.Order)
                            .DistinctBy(c => c.PersonId)
                            .Select(c => new SharedActorDto(c.PersonId, c.Name, c.Character))
                            .ToList();
                        return new NamesakeDto(n.ExternalId, catalogByExternal.TryGetValue(n.ExternalId, out var catalogId) ? catalogId : null, n.Title,
                            n.PosterUrl, n.Premiered?.Year, n.Ended?.Year, n.Network, n.Country, n.Language, n.Kind, n.Rating, shared);
                    })
                    .OrderBy(n => n.Year ?? int.MaxValue).ThenBy(n => n.Title, StringComparer.Ordinal)
                    .ToList();
                byShow[show.Id] = new ShowNamesakesDto(lookups.GetValueOrDefault(show.Id), list, true);
            }
            var indexed = shows.Select(s => new IndexedShow(s.Id, s.Title, s.PosterUrl, s.FirstAirDate?.Year, s.ExternalId, s.Popularity, ShowKinds.Of(s.Keywords),
                s.Keywords))
                .ToList();
            return new Index(byShow, indexed);
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);
}
