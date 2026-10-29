using Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalog.API.Services;

/// <summary>A show in the degrees graph; <paramref name="ShowId"/> is null for a show known only from a cast credit.</summary>
public record DegreesShow(string ExternalId, Guid? ShowId, string Title, string? PosterUrl, int? Year);

public record DegreesPerson(Guid PersonId, string Name, string? PhotoUrl);

/// <summary>
/// The actor graph: shows and the people in their main casts, from the catalog's casts and every catalog actor's
/// provider cast credits. <paramref name="PersonShows"/>[p] lists the shows person p is credited in, and
/// <paramref name="PersonCharacters"/>[p] their character in each, in the same order; <paramref name="ShowPeople"/> is
/// the reverse. Indexes are into <paramref name="Shows"/> and <paramref name="People"/>.
/// </summary>
public record DegreesMap(DegreesShow[] Shows, DegreesPerson[] People, int[][] PersonShows, string?[][] PersonCharacters, int[][] ShowPeople)
{
    public int IndexOfShow(Guid showId) => Array.FindIndex(Shows, s => s.ShowId == showId);
}

/// <summary>One link in a chain: <paramref name="Person"/> is in <paramref name="From"/> and <paramref name="To"/>.</summary>
public record DegreesLink(DegreesPerson Person, DegreesShow From, string? CharacterInFrom, DegreesShow To, string? CharacterInTo);

/// <summary>
/// Six degrees: the shortest chain of shared actors between two shows. Rebuilt from the database at most every 30
/// minutes, like <see cref="Universes"/>; with ~6,000 people the search is a breadth-first walk over a few tens of
/// thousands of credits, well under a millisecond.
/// <list type="bullet">
/// <item><b>Provider credits count:</b> a chain may pass through a show outside the catalog (Cheers between Frasier and
/// Wings), which the result marks, since TVmaze lists main casts only and the catalog alone links few shows.</item>
/// <item><b>Self roles don't link:</b> a host or narrator credit ("Himself") would join every talk show to every other.</item>
/// <item><b>Catalog shows first:</b> each person's credits list catalog shows before the rest, so of two chains the
/// same length the walk tends to find the one through shows the app can open.</item>
/// </list>
/// </summary>
public sealed class DegreesGraph(IServiceScopeFactory scopeFactory, HybridCache cache)
{
    public const int MaxDegrees = 6;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    public Task<DegreesMap> GetAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync("degrees:v1", async token => await BuildAsync(token),
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct).AsTask();

    private async Task<DegreesMap> BuildAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();

        var catalog = await db.Shows.AsNoTracking()
            .Select(s => new { s.Id, s.ExternalId, s.Title, s.PosterUrl, s.FirstAirDate })
            .ToListAsync(ct);
        var cast = await db.Cast.AsNoTracking()
            .Select(c => new { c.PersonId, c.Person.Name, c.Person.PhotoUrl, c.ShowId, c.Character })
            .ToListAsync(ct);
        var credits = await db.CastCredits.AsNoTracking()
            .Select(c => new { c.PersonId, c.ShowExternalId, c.Title, c.PosterUrl, c.Premiered, c.Character })
            .ToListAsync(ct);

        var shows = new List<DegreesShow>();
        var showIndex = new Dictionary<string, int>();
        foreach (var s in catalog)
        {
            showIndex[s.ExternalId] = shows.Count;
            shows.Add(new DegreesShow(s.ExternalId, s.Id, s.Title, s.PosterUrl, s.FirstAirDate?.Year));
        }
        var externalIdOf = catalog.ToDictionary(s => s.Id, s => s.ExternalId);
        foreach (var c in credits.Where(c => !showIndex.ContainsKey(c.ShowExternalId)))
        {
            showIndex[c.ShowExternalId] = shows.Count;
            shows.Add(new DegreesShow(c.ShowExternalId, null, c.Title, c.PosterUrl, c.Premiered?.Year));
        }

        var people = new List<DegreesPerson>();
        var personIndex = new Dictionary<Guid, int>();
        // Per person, show index -> character; a catalog cast entry wins over the credit for the same show.
        var links = new List<Dictionary<int, string?>>();
        int PersonAt(Guid id, string name, string? photo)
        {
            if (!personIndex.TryGetValue(id, out var index))
            {
                personIndex[id] = index = people.Count;
                people.Add(new DegreesPerson(id, name, photo));
                links.Add([]);
            }
            return index;
        }
        foreach (var c in cast)
        {
            if (c.Character is { } character && Universes.IsSelfRole(character, c.Name))
            {
                continue;
            }
            links[PersonAt(c.PersonId, c.Name, c.PhotoUrl)][showIndex[externalIdOf[c.ShowId]]] = c.Character;
        }
        foreach (var c in credits)
        {
            if (!personIndex.TryGetValue(c.PersonId, out var p) || (c.Character is { } character && Universes.IsSelfRole(character, people[p].Name)))
            {
                continue;
            }
            links[p].TryAdd(showIndex[c.ShowExternalId], c.Character);
        }

        var personShows = new int[people.Count][];
        var personCharacters = new string?[people.Count][];
        var showPeople = new List<int>[shows.Count];
        for (var p = 0; p < people.Count; p++)
        {
            var ordered = links[p].OrderBy(l => shows[l.Key].ShowId is null).ThenBy(l => l.Key).ToList();
            personShows[p] = ordered.Select(l => l.Key).ToArray();
            personCharacters[p] = ordered.Select(l => l.Value).ToArray();
            foreach (var s in personShows[p])
            {
                (showPeople[s] ??= []).Add(p);
            }
        }
        return new DegreesMap(shows.ToArray(), people.ToArray(), personShows, personCharacters,
            showPeople.Select(l => l?.ToArray() ?? []).ToArray());
    }

    /// <summary>
    /// The shortest chain from one show to another, as links; empty when they're the same show, null when there's no
    /// chain within <see cref="MaxDegrees"/>.
    /// </summary>
    public static List<DegreesLink>? Find(DegreesMap map, int from, int to)
    {
        if (from == to)
        {
            return [];
        }

        // Breadth-first over shows, remembering which person led to each; depth is in degrees (people).
        var via = new Dictionary<int, (int Show, int Person)> { [from] = (-1, -1) };
        var frontier = new List<int> { from };
        for (var degree = 1; degree <= MaxDegrees && frontier.Count > 0; degree++)
        {
            var next = new List<int>();
            foreach (var show in frontier)
            {
                foreach (var person in map.ShowPeople[show])
                {
                    foreach (var other in map.PersonShows[person])
                    {
                        if (via.ContainsKey(other))
                        {
                            continue;
                        }
                        via[other] = (show, person);
                        if (other == to)
                        {
                            return Trace(map, via, to);
                        }
                        next.Add(other);
                    }
                }
            }
            frontier = next;
        }
        return null;
    }

    private static List<DegreesLink> Trace(DegreesMap map, Dictionary<int, (int Show, int Person)> via, int end)
    {
        var chain = new List<DegreesLink>();
        for (var show = end; via[show].Show >= 0; show = via[show].Show)
        {
            var (previous, person) = via[show];
            chain.Add(new DegreesLink(map.People[person], map.Shows[previous], CharacterIn(map, person, previous), map.Shows[show], CharacterIn(map, person, show)));
        }
        chain.Reverse();
        return chain;
    }

    private static string? CharacterIn(DegreesMap map, int person, int show)
    {
        var at = Array.IndexOf(map.PersonShows[person], show);
        return at >= 0 ? map.PersonCharacters[person][at] : null;
    }
}
