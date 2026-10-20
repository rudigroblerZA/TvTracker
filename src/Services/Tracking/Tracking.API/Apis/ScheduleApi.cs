using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tracking.API.Data;
using Tracking.API.Services;

namespace Tracking.API.Apis;

/// <summary>Up Next, calendar, per-show progress and stats.</summary>
public static class ScheduleApi
{
    public static RouteGroupBuilder MapScheduleApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/up-next", GetUpNextAsync);
        api.MapGet("/calendar", GetCalendarAsync);
        api.MapGet("/progress/{showId:guid}", GetProgressAsync);
        api.MapGet("/stats", GetStatsAsync);

        return api;
    }

    /// <summary>
    /// For each show being watched: the next unwatched episode that has aired,
    /// or, when caught up, the next episode still to air.
    /// </summary>
    private static async Task<Ok<List<UpNextItemDto>>> GetUpNextAsync(
        ClaimsPrincipal user, TrackingContext db, UserCache cache, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();

        var items = await cache.GetUpNextAsync(userId, async token =>
        {
            var now = time.GetUtcNow();

            var follows = await db.Follows.AsNoTracking()
                .Where(f => f.UserId == userId && f.Status == WatchStatus.Watching)
                .ToListAsync(token);
            var showIds = follows.Select(f => f.ShowId).ToList();

            var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, token);
            var episodes = await db.Episodes.AsNoTracking()
                .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0)
                .ToListAsync(token);
            var watched = (await db.WatchedEpisodes.AsNoTracking()
                .Where(w => w.UserId == userId)
                .Select(w => w.EpisodeId)
                .ToListAsync(token)).ToHashSet();

            var result = new List<UpNextItemDto>();
            foreach (var showEpisodes in episodes.GroupBy(e => e.ShowId))
            {
                if (!shows.TryGetValue(showEpisodes.Key, out var show))
                {
                    continue;
                }

                var ordered = showEpisodes.InWatchOrder().ToList();
                var aired = ordered.Where(e => e.AirsAtUtc <= now).ToList();
                var watchedCount = aired.Count(e => watched.Contains(e.EpisodeId));

                var nextAired = aired.FirstOrDefault(e => !watched.Contains(e.EpisodeId));
                var next = nextAired ?? ordered.FirstOrDefault(e => e.AirsAtUtc > now && !watched.Contains(e.EpisodeId));
                if (next is null)
                {
                    continue;
                }

                result.Add(new UpNextItemDto(
                    show.ShowId, show.Title, show.PosterUrl, next.ToRef(),
                    HasAired: nextAired is not null,
                    RemainingAired: aired.Count - watchedCount,
                    WatchedCount: watchedCount,
                    AiredCount: aired.Count));
            }

            // Shows with something to watch now first (most recently aired first), then upcoming by air date.
            var watchNow = result.Where(i => i.HasAired).OrderByDescending(i => i.Episode.AirsAtUtc);
            var upcoming = result.Where(i => !i.HasAired).OrderBy(i => i.Episode.AirsAtUtc);
            return watchNow.Concat(upcoming).ToList();
        }, ct);

        return TypedResults.Ok(items);
    }

    /// <summary>Episodes of followed shows (except stopped ones) airing in the range, with watched flags. Times are UTC.</summary>
    private static async Task<Results<Ok<List<CalendarItemDto>>, BadRequest<string>>> GetCalendarAsync(
        ClaimsPrincipal user, TrackingContext db, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (to <= from || to - from > TimeSpan.FromDays(62))
        {
            return TypedResults.BadRequest("'to' must be after 'from' and the range at most 62 days.");
        }

        var userId = user.GetUserId();
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();

        var showIds = await db.Follows
            .Where(f => f.UserId == userId && f.Status != WatchStatus.Stopped)
            .Select(f => f.ShowId)
            .ToListAsync(ct);

        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.AirsAtUtc >= fromUtc && e.AirsAtUtc < toUtc)
            .OrderBy(e => e.AirsAtUtc)
            .ToListAsync(ct);

        var episodeIds = episodes.Select(e => e.EpisodeId).ToList();
        var watched = (await db.WatchedEpisodes
            .Where(w => w.UserId == userId && episodeIds.Contains(w.EpisodeId))
            .Select(w => w.EpisodeId)
            .ToListAsync(ct)).ToHashSet();

        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        return TypedResults.Ok(episodes
            .Select(e =>
            {
                shows.TryGetValue(e.ShowId, out var show);
                return new CalendarItemDto(e.ShowId, show?.Title ?? "Unknown show", show?.PosterUrl, e.ToRef(), watched.Contains(e.EpisodeId));
            })
            .ToList());
    }

    private static async Task<Ok<ShowProgressDto>> GetProgressAsync(
        Guid showId, ClaimsPrincipal user, TrackingContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();

        var follow = await db.Follows.AsNoTracking().SingleOrDefaultAsync(f => f.UserId == userId && f.ShowId == showId, ct);
        var episodes = (await db.Episodes.AsNoTracking().Where(e => e.ShowId == showId).ToListAsync(ct)).InWatchOrder().ToList();
        var watched = (await db.WatchedEpisodes.AsNoTracking()
            .Where(w => w.UserId == userId && w.ShowId == showId)
            .Select(w => w.EpisodeId)
            .ToListAsync(ct)).ToHashSet();

        var counted = episodes.Where(e => e.CountsTowardsProgress()).ToList();
        var aired = counted.Where(e => e.AirsAtUtc <= now).ToList();
        var watchedAired = aired.Count(e => watched.Contains(e.EpisodeId));

        var seasons = episodes
            .GroupBy(e => e.SeasonNumber)
            .OrderBy(g => g.Key)
            .Select(g => new SeasonProgressDto(
                g.Key,
                g.Count(),
                g.Count(e => e.AirsAtUtc <= now),
                g.Count(e => watched.Contains(e.EpisodeId))))
            .ToList();

        var next = aired.FirstOrDefault(e => !watched.Contains(e.EpisodeId))
            ?? counted.FirstOrDefault(e => e.AirsAtUtc > now && !watched.Contains(e.EpisodeId));

        return TypedResults.Ok(new ShowProgressDto(
            showId,
            follow?.Status,
            counted.Count,
            aired.Count,
            watchedAired,
            aired.Count == 0 ? 0 : Math.Round(100.0 * watchedAired / aired.Count, 1),
            next?.ToRef(),
            seasons,
            watched.ToList()));
    }

    private static async Task<Ok<StatsDto>> GetStatsAsync(ClaimsPrincipal user, TrackingContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();

        var watched = await db.WatchedEpisodes.AsNoTracking()
            .Where(w => w.UserId == userId)
            .GroupJoin(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, eps) => new { w, eps })
            .SelectMany(x => x.eps.DefaultIfEmpty(), (x, e) => new
            {
                x.w.ShowId,
                x.w.WatchedAtUtc,
                Runtime = e == null ? 0 : e.RuntimeMinutes ?? 0
            })
            .ToListAsync(ct);

        var follows = await db.Follows.AsNoTracking().Where(f => f.UserId == userId).ToListAsync(ct);

        var showIds = watched.Select(w => w.ShowId).Distinct().ToList();
        var genresByShow = await db.Shows.AsNoTracking()
            .Where(s => showIds.Contains(s.ShowId))
            .ToDictionaryAsync(s => s.ShowId, s => s.Genres, ct);

        var topGenres = watched
            .SelectMany(w => genresByShow.GetValueOrDefault(w.ShowId) ?? [])
            .GroupBy(g => g)
            .Select(g => new GenreCountDto(g.Key, g.Count()))
            .OrderByDescending(g => g.Episodes)
            .Take(5)
            .ToList();

        var firstWatchByShow = watched.GroupBy(w => w.ShowId).ToDictionary(g => g.Key, g => g.Min(w => w.WatchedAtUtc));

        var years = watched
            .GroupBy(w => w.WatchedAtUtc.Year)
            .OrderByDescending(g => g.Key)
            .Select(g => new YearStatsDto(
                g.Key,
                g.Count(),
                g.Sum(w => w.Runtime),
                firstWatchByShow.Count(kv => kv.Value.Year == g.Key)))
            .ToList();

        return TypedResults.Ok(new StatsDto(
            watched.Count,
            watched.Sum(w => w.Runtime),
            follows.Count,
            follows.Count(f => f.Status == WatchStatus.Completed),
            topGenres,
            years));
    }
}
