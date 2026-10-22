using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="Id">The universe's hub show (see <c>Hub</c>), which also addresses it; any member's id finds it too.</param>
/// <param name="Name">Named after the hub show.</param>
/// <param name="FromYear">Its first premiere.</param>
/// <param name="ToYear">The year of its last episode; null while any of its shows is still running.</param>
/// <param name="Posters">Up to four of its shows, most popular first, for a card.</param>
public record UniverseSummaryDto(Guid Id, string Name, int ShowCount, int CharacterCount, int? FromYear, int? ToYear, IReadOnlyList<ShowSummaryDto> Posters);

/// <param name="Shows">Oldest first.</param>
public record UniverseDto(Guid Id, string Name, IReadOnlyList<UniverseShowDto> Shows, IReadOnlyList<UniverseLinkDto> Links);

/// <param name="Characters">Characters it shares with the universe's other shows.</param>
/// <param name="Seasons">Its seasons with dates, for the timeline: aired and scheduled episodes; seasons without dates are left out.</param>
public record UniverseShowDto(ShowSummaryDto Show, int Characters, IReadOnlyList<TimelineSeasonDto>? Seasons = null);

/// <param name="From">The season's first episode.</param>
/// <param name="To">Its last episode so far, or the last one scheduled.</param>
/// <param name="Aired">Episodes aired by now; the rest are scheduled.</param>
public record TimelineSeasonDto(int Number, DateTimeOffset From, DateTimeOffset To, int Episodes, int Aired);

/// <param name="A">The earlier of the two shows.</param>
/// <param name="Kind">"revival" (same title, later run), "continuation" (most of the later show's characters from
/// earlier shows come from this one), "crossover", or "franchise" (no shared characters: the same franchise name).</param>
public record UniverseLinkDto(Guid A, Guid B, string Kind, IReadOnlyList<SharedCharacterDto> Characters);

public record SharedCharacterDto(Guid PersonId, string PersonName, string? PhotoUrl, string Character);

/// <summary>Shared universes found from the cast and franchise names; public, like the rest of the catalog (see <see cref="Universes"/>).</summary>
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
        var lastAired = await db.Episodes.AsNoTracking()
            .Where(e => ids.Contains(e.ShowId) && e.AirsAtUtc != null)
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, Last = g.Max(e => e.AirsAtUtc!.Value) })
            .ToDictionaryAsync(g => g.ShowId, g => g.Last, ct);

        return TypedResults.Ok(selected
            .Where(u => u.Shows.All(shows.ContainsKey))
            .Select(u =>
            {
                var members = u.Shows.Select(id => shows[id]).ToList();
                var hub = Hub(members, u.Links);
                return new UniverseSummaryDto(
                    hub.Id,
                    Name(members, hub),
                    members.Count,
                    u.Links.SelectMany(l => l.Characters).Select(c => (c.PersonId, Character: c.Character.ToLowerInvariant())).Distinct().Count(),
                    members.Min(s => s.FirstAirDate)?.Year,
                    members.Any(s => s.Status is not (ShowStatus.Ended or ShowStatus.Cancelled))
                        ? null
                        : members.Select(s => lastAired.TryGetValue(s.Id, out var last) ? last.Year : s.FirstAirDate?.Year).Max(),
                    members.OrderByDescending(s => s.Popularity).Take(4).Select(s => s.ToSummary()).ToList());
            })
            .OrderByDescending(u => u.ShowCount)
            .ThenByDescending(u => u.CharacterCount)
            .ToList());
    }

    /// <summary>The universe a show is in; 404 when it shares characters with no other show.</summary>
    private static async Task<Results<Ok<UniverseDto>, NotFound>> GetUniverseAsync(
        Guid id, Universes universes, CatalogContext db, TimeProvider time, CancellationToken ct)
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

        // Season spans for the timeline. Specials (season 0) have no place on it.
        var now = time.GetUtcNow();
        var memberIds = members.Select(s => s.Id).ToList();
        var seasons = (await db.Episodes.AsNoTracking()
                .Where(e => memberIds.Contains(e.ShowId) && e.AirsAtUtc != null && e.SeasonNumber > 0)
                .GroupBy(e => new { e.ShowId, e.SeasonNumber })
                .Select(g => new
                {
                    g.Key.ShowId,
                    g.Key.SeasonNumber,
                    From = g.Min(e => e.AirsAtUtc!.Value),
                    To = g.Max(e => e.AirsAtUtc!.Value),
                    Episodes = g.Count(),
                    Aired = g.Count(e => e.AirsAtUtc <= now)
                })
                .ToListAsync(ct))
            .ToLookup(x => x.ShowId, x => new TimelineSeasonDto(x.SeasonNumber, x.From, x.To, x.Episodes, x.Aired));

        var hub = Hub(members, links);
        return TypedResults.Ok(new UniverseDto(
            hub.Id,
            Name(members, hub),
            members.Select(s => new UniverseShowDto(s.ToSummary(),
                links.Where(l => l.A == s.Id || l.B == s.Id).SelectMany(l => l.Characters).Select(c => c.PersonId).Distinct().Count(),
                seasons[s.Id].OrderBy(x => x.From).ToList())).ToList(),
            dtoLinks));
    }

    /// <summary>
    /// The franchise name when every show has it before a colon and none is called just that ("Star Wars" for "Star
    /// Wars: Visions" and "Star Wars: Skeleton Crew"); otherwise the hub's title.
    /// </summary>
    private static string Name(List<Show> members, Show hub)
    {
        var stems = members.Select(s => s.Title.IndexOf(':') is > 0 and var colon ? s.Title[..colon].Trim() : null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return stems is [{ } stem] ? stem : hub.Title;
    }

    /// <summary>
    /// The show a universe is named after:
    /// <list type="number">
    /// <item>the root title the most others extend ("NCIS" for "NCIS: Sydney" and "NCIS: Origins");</item>
    /// <item>else the show sharing the most characters (The Defenders, not Agent Carter, which premiered first but
    /// shares one);</item>
    /// <item>else the one with the most links, then the earliest (Chicago Fire).</item>
    /// </list>
    /// </summary>
    private static Show Hub(List<Show> members, IReadOnlyList<UniverseLink> links) =>
        members
            .OrderByDescending(s => members.Count(o => o.Title.StartsWith(s.Title.Trim() + ":", StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(s => links.Where(l => l.A == s.Id || l.B == s.Id).Sum(l => l.Characters.Count))
            .ThenByDescending(s => links.Count(l => l.A == s.Id || l.B == s.Id))
            .ThenBy(s => s.FirstAirDate ?? DateOnly.MaxValue)
            .First();

    /// <summary>
    /// "revival" when both have the same title (Frasier, 1993 and 2023); "continuation" when more than half of the
    /// later show's characters from earlier shows come from this one (Daredevil: Born Again from Marvel's Daredevil);
    /// otherwise "crossover" (The Defenders draws on four shows about equally, so it's a crossover of each).
    /// </summary>
    private static string Kind(UniverseLink link, Dictionary<Guid, Show> shows, List<UniverseLink> all)
    {
        if (link.Characters.Count == 0)
        {
            return "franchise";
        }

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
