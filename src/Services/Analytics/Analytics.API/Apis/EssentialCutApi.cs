using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// The essential cut (see <see cref="EssentialCut"/>), computed on request from the user's own watches so it's current,
/// with the ratings and moderators' notes it goes by.
/// </summary>
public static class EssentialCutApi
{
    public static void MapEssentialCutApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/{showId:guid}/essential-cut", GetEssentialCutAsync).RequireFeature(FeatureFlags.EssentialCut);

    private static async Task<Results<Ok<EssentialCutDto>, NotFound, ValidationProblem>> GetEssentialCutAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, string level = "lean", CancellationToken ct = default)
    {
        level = level.ToLowerInvariant();
        if (!EssentialCut.Levels.ContainsKey(level))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["level"] = [$"Use one of {string.Join(", ", EssentialCut.Levels.Keys)}."] });
        }

        var show = await db.Shows.AsNoTracking().SingleOrDefaultAsync(s => s.ShowId == showId, ct);
        if (show is null)
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => e.ShowId == showId && e.SeasonNumber > 0)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .ToListAsync(ct);
        var rating = await RatingsAsync(db, showId, ct);
        var notes = await NotesAsync(db, showId, ct);

        var userId = user.GetUserId();
        var watched = (await db.Watches.AsNoTracking().Where(w => w.UserId == userId && w.ShowId == showId).Select(w => w.EpisodeId).ToListAsync(ct)).ToHashSet();

        return TypedResults.Ok(EssentialCut.Of(show, level, episodes, rating, notes, watched, now));
    }

    /// <summary>
    /// Moderators' notes on the show's episodes (see <see cref="EssentialCut"/>), by episode.
    /// </summary>
    private static Task<Dictionary<Guid, EpisodeNoteCopy>> NotesAsync(AnalyticsContext db, Guid showId, CancellationToken ct) =>
        db.EpisodeNotes.AsNoTracking().Where(n => n.ShowId == showId).ToDictionaryAsync(n => n.EpisodeId, ct);

    /// <summary>
    /// The ratings the cut goes by: the provider's, averaged with our users' once enough rated an episode.
    /// </summary>
    internal static async Task<Func<EpisodeCopy, double?>> RatingsAsync(AnalyticsContext db, Guid showId, CancellationToken ct) =>
        (await RatingsAsync(db, [showId], ct))(showId);

    /// <summary>
    /// The same, for several shows in one query: a rating function per show.
    /// </summary>
    internal static async Task<Func<Guid, Func<EpisodeCopy, double?>>> RatingsAsync(AnalyticsContext db, IReadOnlyCollection<Guid> showIds,
        CancellationToken ct)
    {
        var ours = (await db.EpisodeRatings.AsNoTracking()
                .Where(r => showIds.Contains(r.ShowId))
                .GroupBy(r => new { r.ShowId, r.EpisodeId })
                .Where(g => g.Count() >= InsightRules.MinRatingsToShow)
                .Select(g => new { g.Key.ShowId, g.Key.EpisodeId, Average = g.Average(r => r.Stars * 2.0) })
                .ToListAsync(ct))
            .ToLookup(g => g.ShowId);
        return showId =>
        {
            var mine = ours[showId].ToDictionary(g => g.EpisodeId, g => g.Average);
            return e => EssentialCut.Rating(e.ExternalRating, mine.TryGetValue(e.EpisodeId, out var us) ? us : null);
        };
    }
}
