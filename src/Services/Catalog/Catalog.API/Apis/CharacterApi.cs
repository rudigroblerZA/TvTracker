using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="Character">The name the actor's character goes by in the show the trail started from.</param>
/// <param name="Shows">Every show the actor plays the character in, by premiere.</param>
public record CharacterTrailDto(Guid PersonId, string PersonName, string? PhotoUrl, string Character, IReadOnlyList<TrailShowDto> Shows);

/// <param name="ShowId">Null for a show that isn't in the catalog; the UI offers a search, which imports it.</param>
/// <param name="ExternalId">The provider's id, for shows outside the catalog.</param>
/// <param name="Character">The character's name in this show ("Dr. Frasier Crane" in Cheers).</param>
/// <param name="FirstAiredUtc">The show's first regular episode (catalog shows only).</param>
/// <param name="LastAiredUtc">Its latest aired regular episode (catalog shows only).</param>
/// <param name="Episodes">Its regular episodes aired so far (catalog shows only).</param>
public record TrailShowDto(Guid? ShowId, string? ExternalId, string Title, string? PosterUrl, int? Year, ShowStatus Status,
    string Character, bool Voice, double? Rating, DateTimeOffset? FirstAiredUtc, DateTimeOffset? LastAiredUtc, int? Episodes);

/// <summary>
/// A character's trail: every show where the same actor plays a same-named character (<see cref="Universes.SameCharacter"/>),
/// from the catalog's casts and the actor's provider cast credits, so shows outside the catalog count too ("Frasier
/// Crane" in Cheers, Frasier, Wings and the Frasier revival). Same actor only: TVmaze's character ids are per show, and
/// a recast character can't be told from a namesake. TVmaze lists main casts only, so guest appearances (Frasier in
/// Wings) are missing, and a character who joins in a later season counts for the whole show.
/// </summary>
public static class CharacterApi
{
    public static void MapCharacterApi(this RouteGroupBuilder api)
    {
        api.MapGet("/people/{personId:guid}/characters/{showId:guid}", GetTrailAsync).RequireFeature(FeatureFlags.CharacterTrail);
    }

    private static async Task<Results<Ok<CharacterTrailDto>, NotFound>> GetTrailAsync(
        Guid personId, Guid showId, CatalogContext db, TimeProvider time, CancellationToken ct)
    {
        var start = await db.Cast.AsNoTracking()
            .Where(c => c.PersonId == personId && c.ShowId == showId && c.Character != null)
            .Select(c => new { c.Character, c.Person.Name, c.Person.PhotoUrl })
            .SingleOrDefaultAsync(ct);
        if (start is null || Universes.IsSelfRole(start.Character!, start.Name))
        {
            return TypedResults.NotFound();
        }

        var appearances = (await AppearancesAsync(db, [personId], ct))
            .Where(a => Universes.SameCharacter(start.Character!, a.Character))
            .ToList();

        var catalogIds = appearances.Where(a => a.ShowId is not null).Select(a => a.ShowId!.Value).ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => catalogIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var now = time.GetUtcNow();
        var aired = (await db.Episodes.AsNoTracking()
                .Where(e => catalogIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc != null && e.AirsAtUtc <= now)
                .GroupBy(e => e.ShowId)
                .Select(g => new { ShowId = g.Key, First = g.Min(e => e.AirsAtUtc), Last = g.Max(e => e.AirsAtUtc), Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(a => a.ShowId);

        var trail = appearances
            .Select(a =>
            {
                if (a.ShowId is { } id && shows.TryGetValue(id, out var show))
                {
                    var episodes = aired.GetValueOrDefault(id);
                    return new TrailShowDto(id, null, show.Title, show.PosterUrl, show.FirstAirDate?.Year, show.Status, a.Character,
                        a.Voice, show.ExternalRating, episodes?.First, episodes?.Last, episodes?.Count ?? 0);
                }
                return new TrailShowDto(null, a.ExternalId, a.Title, a.PosterUrl, a.Premiered?.Year, a.Status, a.Character,
                    a.Voice, a.Rating, null, null, null);
            })
            .OrderBy(t => t.FirstAiredUtc?.Year ?? t.Year ?? int.MaxValue)
            .ThenBy(t => t.FirstAiredUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(t => t.Title)
            .ToList();

        return TypedResults.Ok(new CharacterTrailDto(personId, start.Name, start.PhotoUrl, start.Character!, trail));
    }

    /// <summary>
    /// For each of <paramref name="cast"/> (person and character in one show), how many shows the trail would list,
    /// counting that one: 1 when the character appears nowhere else. Self roles count 0.
    /// </summary>
    internal static async Task<Dictionary<Guid, int>> TrailLengthsAsync(
        CatalogContext db, IReadOnlyCollection<(Guid PersonId, string Name, string? Character)> cast, CancellationToken ct)
    {
        var playing = cast.Where(c => c.Character is not null && !Universes.IsSelfRole(c.Character, c.Name)).ToList();
        var byPerson = (await AppearancesAsync(db, playing.Select(c => c.PersonId).Distinct().ToList(), ct)).ToLookup(a => a.PersonId);
        return playing.ToDictionary(c => c.PersonId, c => byPerson[c.PersonId].Count(a => Universes.SameCharacter(c.Character!, a.Character)));
    }

    private sealed record Appearance(Guid PersonId, Guid? ShowId, string? ExternalId, string Title, string? PosterUrl,
        DateOnly? Premiered, ShowStatus Status, double? Rating, string Character, bool Voice);

    /// <summary>
    /// Every character the people play, one per show: their catalog cast rows, plus provider credits for shows the
    /// catalog doesn't have (a credit's show is matched to the catalog by its id, or by its provider id when the credit
    /// was fetched before the show was imported).
    /// </summary>
    private static async Task<List<Appearance>> AppearancesAsync(CatalogContext db, List<Guid> personIds, CancellationToken ct)
    {
        var cast = await db.Cast.AsNoTracking()
            .Where(c => personIds.Contains(c.PersonId) && c.Character != null)
            .Select(c => new Appearance(c.PersonId, c.ShowId, null, "", null, null, default, null, c.Character!, false))
            .ToListAsync(ct);
        var credits = await db.CastCredits.AsNoTracking()
            .Where(c => personIds.Contains(c.PersonId) && c.Character != null)
            .ToListAsync(ct);

        var externalIds = credits.Where(c => c.ShowId is null).Select(c => c.ShowExternalId).Distinct().ToList();
        var imported = await db.Shows.AsNoTracking()
            .Where(s => s.Source == "tvmaze" && externalIds.Contains(s.ExternalId))
            .ToDictionaryAsync(s => s.ExternalId, s => s.Id, ct);
        var inCatalog = cast.Select(c => (c.PersonId, c.ShowId)).ToHashSet();

        var others = credits
            .Select(c => (Credit: c, ShowId: c.ShowId ?? (imported.TryGetValue(c.ShowExternalId, out var id) ? id : null)))
            .Where(c => c.ShowId is null || !inCatalog.Contains((c.Credit.PersonId, c.ShowId)))
            .Select(c => new Appearance(c.Credit.PersonId, c.ShowId, c.ShowId is null ? c.Credit.ShowExternalId : null, c.Credit.Title,
                c.Credit.PosterUrl, c.Credit.Premiered, c.Credit.Status, c.Credit.Rating, c.Credit.Character!, c.Credit.Voice));
        return [.. cast, .. others];
    }
}
