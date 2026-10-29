using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Degrees">How many actors the chain goes through; 0 when the two are the same show.</param>
/// <param name="Links">The chain, from the first show to the second.</param>
/// <param name="OutsideCatalog">How many shows along the way aren't in the catalog (they can't be opened).</param>
public record DegreesDto(DegreesShowDto From, DegreesShowDto To, int Degrees, IReadOnlyList<DegreesLinkDto> Links, int OutsideCatalog);

/// <param name="ShowId">Null for a show known only from an actor's provider credits.</param>
public record DegreesShowDto(Guid? ShowId, string Title, string? PosterUrl, int? Year);

/// <param name="CharacterInFrom">Who the actor plays in <paramref name="From"/>; null when the provider didn't say.</param>
public record DegreesLinkDto(Guid PersonId, string Name, string? PhotoUrl, DegreesShowDto From, string? CharacterInFrom,
    DegreesShowDto To, string? CharacterInTo);

/// <param name="Degrees">The chain's length, for the player to beat; the chain itself is a second call.</param>
public record DegreesChallengeDto(DegreesShowDto From, DegreesShowDto To, int Degrees);

/// <summary>
/// Six degrees: the shortest chain of shared main-cast actors between two shows (<see cref="DegreesGraph"/>), and a
/// challenge: two popular shows picked at random that are connected, with the chain's length but not the chain.
/// </summary>
public static class DegreesApi
{
    private const double ChallengePopularity = 95;
    private const int ChallengeTries = 20;

    public static void MapDegreesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/degrees", GetAsync).RequireFeature(FeatureFlags.SixDegrees);
        api.MapGet("/degrees/challenge", GetChallengeAsync).RequireFeature(FeatureFlags.SixDegrees);
    }

    private static async Task<Results<Ok<DegreesDto>, NotFound, NoContent>> GetAsync(Guid from, Guid to, DegreesGraph graph, CancellationToken ct)
    {
        var map = await graph.GetAsync(ct);
        var (a, b) = (map.IndexOfShow(from), map.IndexOfShow(to));
        if (a < 0 || b < 0)
        {
            return TypedResults.NotFound();
        }
        // 204: both shows exist, but no chain of six or fewer joins them.
        if (DegreesGraph.Find(map, a, b) is not { } chain)
        {
            return TypedResults.NoContent();
        }
        var links = chain.Select(l => new DegreesLinkDto(l.Person.PersonId, l.Person.Name, l.Person.PhotoUrl,
            ToDto(l.From), l.CharacterInFrom, ToDto(l.To), l.CharacterInTo)).ToList();
        return TypedResults.Ok(new DegreesDto(ToDto(map.Shows[a]), ToDto(map.Shows[b]), links.Count, links,
            links.Count(l => l.To.ShowId is null)));
    }

    /// <summary>Two popular catalog shows with a chain between them, at least two degrees apart so it takes some thought.</summary>
    private static async Task<Results<Ok<DegreesChallengeDto>, NoContent>> GetChallengeAsync(DegreesGraph graph, CatalogContext db, CancellationToken ct)
    {
        var map = await graph.GetAsync(ct);
        var popular = await db.Shows.AsNoTracking().Where(s => s.Popularity >= ChallengePopularity && s.Cast.Count > 0)
            .Select(s => s.Id).ToListAsync(ct);
        var indexes = popular.Select(map.IndexOfShow).Where(i => i >= 0).ToList();
        for (var tries = 0; tries < ChallengeTries && indexes.Count >= 2; tries++)
        {
            var a = indexes[Random.Shared.Next(indexes.Count)];
            var b = indexes[Random.Shared.Next(indexes.Count)];
            if (a != b && DegreesGraph.Find(map, a, b) is { Count: >= 2 } chain)
            {
                return TypedResults.Ok(new DegreesChallengeDto(ToDto(map.Shows[a]), ToDto(map.Shows[b]), chain.Count));
            }
        }
        return TypedResults.NoContent();
    }

    private static DegreesShowDto ToDto(DegreesShow show) => new(show.ShowId, show.Title, show.PosterUrl, show.Year);
}
