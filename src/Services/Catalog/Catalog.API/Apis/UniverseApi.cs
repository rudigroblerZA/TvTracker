using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Apis;

/// <param name="Id">The universe's hub show (see <c>Hub</c>), which also addresses it; any member's id finds it too.</param>
/// <param name="Name">Named after the hub show.</param>
/// <param name="Posters">Up to four of its shows, most popular first, for a card.</param>
public record UniverseSummaryDto(Guid Id, string Name, int ShowCount, int CharacterCount, int? FromYear, int? ToYear, IReadOnlyList<ShowSummaryDto> Posters);

/// <param name="Shows">Oldest first.</param>
public record UniverseDto(Guid Id, string Name, IReadOnlyList<UniverseShowDto> Shows, IReadOnlyList<UniverseLinkDto> Links);

/// <param name="Characters">Characters it shares with the universe's other shows.</param>
public record UniverseShowDto(ShowSummaryDto Show, int Characters);

/// <param name="A">The earlier of the two shows.</param>
/// <param name="Kind">"revival" (same title, later run), "continuation" (most of the later show's characters from
/// earlier shows come from this one) or "crossover".</param>
public record UniverseLinkDto(Guid A, Guid B, string Kind, IReadOnlyList<SharedCharacterDto> Characters);

public record SharedCharacterDto(Guid PersonId, string PersonName, string? PhotoUrl, string Character);

/// <summary>Shared universes found from the cast; public, like the rest of the catalog (see <see cref="Universes"/>).</summary>
public static class UniverseApi
{
    public static void MapUniverseApi(this RouteGroupBuilder api)
    {
        api.MapGet("/universes", GetUniversesAsync);
        api.MapGet("/shows/{id:guid}/universe", GetUniverseAsync);
    }

    private static async Task<Ok<List<UniverseSummaryDto>>> GetUniversesAsync(
        Universes universes, CatalogContext db, int minShows = 2, CancellationToken ct = default)
    {
        var map = await universes.GetAsync(ct);
        var selected = map.Universes.Where(u => u.Shows.Count >= Math.Max(2, minShows)).ToList();
        var ids = selected.SelectMany(u => u.Shows).ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        return TypedResults.Ok(selected
            .Where(u => u.Shows.All(shows.ContainsKey))
            .Select(u =>
            {
                var members = u.Shows.Select(id => shows[id]).ToList();
                var hub = Hub(members, u.Links);
                return new UniverseSummaryDto(
                    hub.Id,
                    hub.Title,
                    members.Count,
                    u.Links.SelectMany(l => l.Characters).Select(c => (c.PersonId, Character: c.Character.ToLowerInvariant())).Distinct().Count(),
                    members.Min(s => s.FirstAirDate)?.Year,
                    members.Max(s => s.FirstAirDate)?.Year,
                    members.OrderByDescending(s => s.Popularity).Take(4).Select(s => s.ToSummary()).ToList());
            })
            .OrderByDescending(u => u.ShowCount)
            .ThenByDescending(u => u.CharacterCount)
            .ToList());
    }

    /// <summary>The universe a show is in; 404 when it shares characters with no other show.</summary>
    private static async Task<Results<Ok<UniverseDto>, NotFound>> GetUniverseAsync(
        Guid id, Universes universes, CatalogContext db, CancellationToken ct)
    {
        var map = await universes.GetAsync(ct);
        if (!map.UniverseOfShow.TryGetValue(id, out var index))
        {
            return TypedResults.NotFound();
        }

        var universe = map.Universes[index];
        var shows = await db.Shows.AsNoTracking().Where(s => universe.Shows.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var members = universe.Shows.Where(shows.ContainsKey).Select(s => shows[s])
            .OrderBy(s => s.FirstAirDate ?? DateOnly.MaxValue).ThenByDescending(s => s.Popularity).ToList();
        var order = members.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var links = universe.Links.Where(l => order.ContainsKey(l.A) && order.ContainsKey(l.B)).ToList();

        var dtoLinks = links
            .Select(l => order[l.A] <= order[l.B] ? l : l with { A = l.B, B = l.A })
            .Select(l => new UniverseLinkDto(l.A, l.B, Kind(l, shows, links),
                l.Characters.Select(c => new SharedCharacterDto(c.PersonId, c.PersonName, c.PhotoUrl, c.Character)).ToList()))
            .OrderBy(l => order[l.A]).ThenBy(l => order[l.B])
            .ToList();

        var hub = Hub(members, links);
        return TypedResults.Ok(new UniverseDto(
            hub.Id,
            hub.Title,
            members.Select(s => new UniverseShowDto(s.ToSummary(),
                links.Where(l => l.A == s.Id || l.B == s.Id).SelectMany(l => l.Characters).Select(c => c.PersonId).Distinct().Count())).ToList(),
            dtoLinks));
    }

    /// <summary>
    /// The show that shares the most characters with the rest (Marvel's Daredevil, not Agent Carter, which premiered
    /// first but shares one); ties go to the earliest.
    /// </summary>
    private static Show Hub(List<Show> members, IReadOnlyList<UniverseLink> links) =>
        members
            .OrderByDescending(s => links.Where(l => l.A == s.Id || l.B == s.Id).Sum(l => l.Characters.Count))
            .ThenBy(s => s.FirstAirDate ?? DateOnly.MaxValue)
            .First();

    /// <summary>
    /// "revival" when both have the same title (Frasier, 1993 and 2023); "continuation" when more than half of the
    /// later show's characters from earlier shows come from this one (Daredevil: Born Again from Marvel's Daredevil);
    /// otherwise "crossover" (The Defenders draws on four shows about equally, so it's a crossover of each).
    /// </summary>
    private static string Kind(UniverseLink link, Dictionary<Guid, Show> shows, List<UniverseLink> all)
    {
        var (earlier, later) = (shows[link.A], shows[link.B]);
        if ((earlier.FirstAirDate ?? DateOnly.MaxValue) > (later.FirstAirDate ?? DateOnly.MaxValue))
        {
            (earlier, later) = (later, earlier);
        }
        if (string.Equals(earlier.Title.Trim(), later.Title.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "revival";
        }

        var fromEarlier = all
            .Where(l => (l.A == later.Id || l.B == later.Id)
                        && (shows[l.A == later.Id ? l.B : l.A].FirstAirDate ?? DateOnly.MaxValue) <= (later.FirstAirDate ?? DateOnly.MaxValue))
            .Sum(l => l.Characters.Count);
        return link.Characters.Count >= 2 && link.Characters.Count * 2 > fromEarlier ? "continuation" : "crossover";
    }
}
