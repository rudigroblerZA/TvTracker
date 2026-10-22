using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tracking.API.Data;
using TvTracker.IntegrationEvents;
using TvTracker;

namespace Tracking.API.Apis;

/// <param name="Status">Watching or PlanToWatch.</param>
/// <param name="LastWatchedAtUtc">When the user last marked one of its episodes; null if never.</param>
/// <param name="Episodes">The next unwatched aired episodes, in order; the plan takes them from the front.</param>
public record TonightShowDto(
    Guid ShowId,
    string Title,
    string? PosterUrl,
    IReadOnlyList<string> Genres,
    WatchStatus Status,
    DateTime? LastWatchedAtUtc,
    int WatchedCount,
    IReadOnlyList<TonightEpisodeDto> Episodes);

/// <param name="RuntimeMinutes">The episode's runtime, or the show's typical one when the provider has none.</param>
public record TonightEpisodeDto(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, int RuntimeMinutes, bool RuntimeEstimated);

/// <summary>
/// Tonight's plan, the Tracking half: what the user could watch next, show by show. The UI plans from it, with Catalog's
/// mood scores for the same shows (<c>POST /api/v1/shows/mood</c>), so this service needs no knowledge of embeddings.
/// </summary>
public static class TonightApi
{
    private const int MaxEpisodesPerShow = 10;

    // When a show has no runtimes at all: a typical hour-long slot's content.
    private const int FallbackRuntime = 45;

    public static void MapTonightApi(this RouteGroupBuilder api) => api.MapGet("/tonight/queue", GetQueueAsync).RequireFeature(FeatureFlags.TonightPlan);

    /// <summary>
    /// For each show the user is watching or plans to watch, its next unwatched episodes that have aired (specials
    /// aside), up to <paramref name="perShow"/>. Shows with nothing left to watch are left out.
    /// </summary>
    private static async Task<Ok<List<TonightShowDto>>> GetQueueAsync(
        ClaimsPrincipal user, TrackingContext db, TimeProvider time, int perShow = 6, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        perShow = Math.Clamp(perShow, 1, MaxEpisodesPerShow);

        var follows = await db.Follows.AsNoTracking()
            .Where(f => f.UserId == userId && (f.Status == WatchStatus.Watching || f.Status == WatchStatus.PlanToWatch))
            .ToListAsync(ct);
        var showIds = follows.Select(f => f.ShowId).ToList();

        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        var episodes = (await db.Episodes.AsNoTracking()
                .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
                .ToListAsync(ct))
            .ToLookup(e => e.ShowId);
        var watched = await db.WatchedEpisodes.AsNoTracking()
            .Where(w => w.UserId == userId && showIds.Contains(w.ShowId))
            .Select(w => new { w.ShowId, w.EpisodeId, w.WatchedAtUtc })
            .ToListAsync(ct);
        var watchedIds = watched.Select(w => w.EpisodeId).ToHashSet();
        var lastWatched = watched.GroupBy(w => w.ShowId).ToDictionary(g => g.Key, g => g.Max(w => w.WatchedAtUtc));

        var queue = new List<TonightShowDto>();
        foreach (var follow in follows)
        {
            if (!shows.TryGetValue(follow.ShowId, out var show))
            {
                continue;
            }

            var showEpisodes = episodes[follow.ShowId].ToList();
            var runtimes = showEpisodes.Where(e => e.RuntimeMinutes > 0).Select(e => e.RuntimeMinutes!.Value).Order().ToList();
            var typical = runtimes.Count > 0 ? runtimes[runtimes.Count / 2] : FallbackRuntime;

            var next = showEpisodes.InWatchOrder()
                .Where(e => !watchedIds.Contains(e.EpisodeId))
                .Take(perShow)
                .Select(e => new TonightEpisodeDto(e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.Title,
                    e.RuntimeMinutes is > 0 ? e.RuntimeMinutes.Value : typical, e.RuntimeMinutes is not > 0))
                .ToList();
            if (next.Count == 0)
            {
                continue;
            }

            queue.Add(new TonightShowDto(show.ShowId, show.Title, show.PosterUrl, show.Genres, follow.Status,
                lastWatched.TryGetValue(show.ShowId, out var last) ? last : null,
                showEpisodes.Count(e => watchedIds.Contains(e.EpisodeId)), next));
        }

        return TypedResults.Ok(queue);
    }
}
