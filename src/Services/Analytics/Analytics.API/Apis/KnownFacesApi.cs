using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="CastCount">The show's cast members with a person, known or not.</param>
public record KnownFacesDto(Guid ShowId, int CastCount, IReadOnlyList<KnownFaceDto> People);

/// <param name="Character">Who they play in this show.</param>
/// <param name="From">The user's other shows they're in, caught-up ones first, then by how much the user watched.</param>
public record KnownFaceDto(Guid PersonId, string Name, string? Character, IReadOnlyList<KnownFromDto> From);

/// <param name="Character">Who they play there.</param>
/// <param name="Watched">Regular episodes of that show the user watched.</param>
/// <param name="Aired">Regular episodes of that show aired so far.</param>
/// <param name="CaughtUp">
/// The user has seen that show's latest aired episode, or rated the show without marking episodes. Otherwise the actor
/// may join it after where the user is, so the UI treats the line as a spoiler.
/// </param>
public record KnownFromDto(Guid ShowId, string Title, string? PosterUrl, string? Character, int Watched, int Aired, bool CaughtUp);

/// <summary>
/// "Where do I know them from?": for each actor in a show's cast, the other shows the user has seen them in. Computed
/// on request from the user's own watches and ratings and the catalog's main casts, so it needs no minimum counts.
/// <list type="bullet">
/// <item><b>Seen:</b> a show with a watched episode, or rated. Followed alone doesn't count: it says nothing about
/// having watched it.</item>
/// <item><b>Main casts only:</b> TVmaze lists no guest roles, so a guest spot in a show the user watched isn't found.</item>
/// <item><b>Spoilers:</b> a cast is today's main cast, with no season per role, so an actor in a show the user is
/// partway through may only arrive later. Those lines say so (<see cref="KnownFromDto.CaughtUp"/>) rather than being
/// left out, since most are from the start.</item>
/// </list>
/// </summary>
public static class KnownFacesApi
{
    private const int MaxFromPerPerson = 3;

    public static void MapKnownFacesApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/{showId:guid}/known-faces", GetAsync).RequireFeature(FeatureFlags.KnownFaces);

    private static async Task<Results<Ok<KnownFacesDto>, UnauthorizedHttpResult>> GetAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var cast = await db.Cast.AsNoTracking()
            .Where(c => c.ShowId == showId && c.PersonId != null)
            .OrderBy(c => c.Order)
            .ToListAsync(ct);
        var personIds = cast.Select(c => c.PersonId!.Value).Distinct().ToList();

        var rated = await db.ShowRatings.AsNoTracking()
            .Where(r => r.UserId == userId && r.ShowId != showId)
            .Select(r => r.ShowId)
            .ToListAsync(ct);
        var watchedShows = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId && w.ShowId != showId)
            .Select(w => w.ShowId)
            .Distinct()
            .ToListAsync(ct);
        var seen = rated.Concat(watchedShows).Distinct().ToList();

        var elsewhere = (await db.Cast.AsNoTracking()
                .Where(c => c.PersonId != null && personIds.Contains(c.PersonId.Value) && seen.Contains(c.ShowId))
                .ToListAsync(ct))
            .ToLookup(c => c.PersonId!.Value);
        var fromIds = elsewhere.SelectMany(g => g).Select(c => c.ShowId).Distinct().ToList();

        var shows = await db.Shows.AsNoTracking().Where(s => fromIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        var progress = await ProgressAsync(db, userId, fromIds, time.GetUtcNow(), ct);
        var ratedSet = rated.ToHashSet();

        var people = cast
            .DistinctBy(c => c.PersonId)
            .Select(member => new KnownFaceDto(member.PersonId!.Value, member.PersonName, member.Character,
                elsewhere[member.PersonId!.Value]
                    .Where(c => shows.ContainsKey(c.ShowId))
                    .DistinctBy(c => c.ShowId)
                    .Select(c =>
                    {
                        var (watched, aired, lastSeen) = progress.GetValueOrDefault(c.ShowId);
                        var show = shows[c.ShowId];
                        return new KnownFromDto(c.ShowId, show.Title, show.PosterUrl, c.Character, watched, aired,
                            lastSeen || watched == 0 && ratedSet.Contains(c.ShowId));
                    })
                    .OrderByDescending(f => f.CaughtUp)
                    .ThenByDescending(f => f.Watched)
                    .Take(MaxFromPerPerson)
                    .ToList()))
            .Where(p => p.From.Count > 0)
            .ToList();

        return TypedResults.Ok(new KnownFacesDto(showId, personIds.Count, people));
    }

    /// <summary>
    /// Per show: regular episodes watched and aired, and whether the latest aired one is watched (season 0 left out, as
    /// Tracking does). Caught up is the latest episode rather than every one, since people skip the odd episode.
    /// </summary>
    private static async Task<Dictionary<Guid, (int Watched, int Aired, bool CaughtUp)>> ProgressAsync(
        AnalyticsContext db, string userId, List<Guid> showIds, DateTimeOffset now, CancellationToken ct)
    {
        var aired = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
            .Select(e => new { e.EpisodeId, e.ShowId, e.SeasonNumber, e.EpisodeNumber })
            .ToListAsync(ct);
        var watched = (await db.Watches.AsNoTracking()
                .Where(w => w.UserId == userId && showIds.Contains(w.ShowId))
                .Select(w => w.EpisodeId)
                .ToListAsync(ct))
            .ToHashSet();

        return aired.GroupBy(e => e.ShowId).ToDictionary(g => g.Key, g =>
        {
            var latest = g.MaxBy(e => (e.SeasonNumber, e.EpisodeNumber))!;
            return (g.Count(e => watched.Contains(e.EpisodeId)), g.Count(), watched.Contains(latest.EpisodeId));
        });
    }
}
