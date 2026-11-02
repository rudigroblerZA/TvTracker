using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tracking.API.Data;
using Tracking.API.Services;
using TvTracker;

namespace Tracking.API.Apis;

public static class FollowsApi
{
    public static RouteGroupBuilder MapFollowsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/follows", GetFollowsAsync);
        api.MapGet("/follows/{showId:guid}", GetFollowAsync);
        api.MapPost("/follows/{showId:guid}", FollowAsync);
        api.MapDelete("/follows/{showId:guid}", UnfollowAsync);
        api.MapPut("/follows/{showId:guid}/status", UpdateStatusAsync);

        api.MapPost("/watched", MarkWatchedAsync);
        api.MapPost("/watched/import", ImportWatchesAsync).RequireFeature(FeatureFlags.HistoryImport);
        api.MapDelete("/watched/{episodeId:guid}", UnmarkWatchedAsync);

        return api;
    }

    private static async Task<Ok<List<FollowDto>>> GetFollowsAsync(
        ClaimsPrincipal user, TrackingContext db, TimeProvider time, WatchStatus? status, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();

        var query = db.Follows.AsNoTracking().Where(f => f.UserId == userId);
        if (status is not null)
        {
            query = query.Where(f => f.Status == status);
        }

        var follows = await query.ToListAsync(ct);
        var showIds = follows.Select(f => f.ShowId).ToList();

        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        var aired = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ShowId, x => x.Count, ct);

        var watched = await db.WatchedEpisodes.AsNoTracking()
            .Where(w => w.UserId == userId)
            .GroupBy(w => w.ShowId)
            .Select(g => new { ShowId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ShowId, x => x.Count, ct);

        var result = follows
            .Select(f =>
            {
                shows.TryGetValue(f.ShowId, out var show);
                return new FollowDto(
                    f.ShowId, show?.Title, show?.PosterUrl, show?.Status, f.Status,
                    watched.GetValueOrDefault(f.ShowId), aired.GetValueOrDefault(f.ShowId), f.FollowedAtUtc);
            })
            .OrderBy(f => f.Title)
            .ToList();

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<FollowDto>, NotFound>> GetFollowAsync(
        Guid showId, ClaimsPrincipal user, TrackingContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var follow = await db.Follows.AsNoTracking().SingleOrDefaultAsync(f => f.UserId == userId && f.ShowId == showId, ct);
        if (follow is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await ToDtoAsync(follow, db, time, ct));
    }

    private static async Task<Ok<FollowDto>> FollowAsync(
        Guid showId, FollowRequest? request, ClaimsPrincipal user, TrackingService tracking, TrackingContext db, TimeProvider time, CancellationToken ct)
    {
        var follow = await tracking.FollowAsync(user.GetUserId(), showId, request?.Status ?? WatchStatus.Watching, ct);
        return TypedResults.Ok(await ToDtoAsync(follow, db, time, ct));
    }

    private static async Task<Results<NoContent, NotFound>> UnfollowAsync(
        Guid showId, ClaimsPrincipal user, TrackingService tracking, CancellationToken ct) =>
        await tracking.UnfollowAsync(user.GetUserId(), showId, ct) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static async Task<Results<Ok<FollowDto>, NotFound>> UpdateStatusAsync(
        Guid showId, UpdateStatusRequest request, ClaimsPrincipal user, TrackingService tracking, TrackingContext db, TimeProvider time, CancellationToken ct)
    {
        var follow = await tracking.SetStatusAsync(user.GetUserId(), showId, request.Status, ct);
        return follow is null ? TypedResults.NotFound() : TypedResults.Ok(await ToDtoAsync(follow, db, time, ct));
    }

    private static async Task<Results<Ok<MarkWatchedResult>, ValidationProblem>> MarkWatchedAsync(
        MarkWatchedRequest request, ClaimsPrincipal user, TrackingContext db, TrackingService tracking, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        List<EpisodeCopy> episodes;

        if (request.EpisodeIds is { Count: > 0 } ids)
        {
            var idList = ids.Distinct().Take(1000).ToList();
            episodes = await db.Episodes.Where(e => idList.Contains(e.EpisodeId)).ToListAsync(ct);
        }
        else if (request.ShowId is { } showId)
        {
            var aired = await db.Episodes
                .Where(e => e.ShowId == showId && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
                .ToListAsync(ct);

            if (request.SeasonNumber is { } season)
            {
                episodes = aired.Where(e => e.SeasonNumber == season).ToList();
            }
            else if (request.UpToEpisodeId is { } upTo)
            {
                var target = await db.Episodes.FindAsync([upTo], ct);
                if (target is null || target.ShowId != showId)
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["upToEpisodeId"] = ["The episode isn't part of this show."]
                    });
                }

                episodes = aired
                    .Where(e => e.SeasonNumber < target.SeasonNumber
                        || (e.SeasonNumber == target.SeasonNumber && e.EpisodeNumber <= target.EpisodeNumber))
                    .ToList();

                // Allow marking the target itself even if it hasn't officially aired yet (e.g. watched early).
                if (!episodes.Any(e => e.EpisodeId == target.EpisodeId))
                {
                    episodes.Add(target);
                }
            }
            else
            {
                episodes = aired;
            }
        }
        else
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["Provide episodeIds, or a showId (optionally with seasonNumber or upToEpisodeId)."]
            });
        }

        var added = await tracking.MarkWatchedAsync(user.GetUserId(), episodes, request.WatchedAtUtc, ct);
        return TypedResults.Ok(new MarkWatchedResult(added.Count, added));
    }

    /// <summary>Bring your history: up to <see cref="ImportWatchesRequest.MaxWatches"/> watches with their own times (see <see cref="TrackingService.ImportWatchesAsync"/>).</summary>
    private static async Task<Results<Ok<ImportWatchesResult>, ValidationProblem>> ImportWatchesAsync(
        ImportWatchesRequest request, ClaimsPrincipal user, TrackingService tracking, CancellationToken ct)
    {
        if (request.Watches is not { Count: > 0 and <= ImportWatchesRequest.MaxWatches })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["watches"] = [$"Give 1 to {ImportWatchesRequest.MaxWatches} watches."] });
        }
        var added = await tracking.ImportWatchesAsync(user.GetUserId(), request.Watches.Select(w => (w.EpisodeId, w.WatchedAtUtc)).ToList(), ct);
        return TypedResults.Ok(new ImportWatchesResult(added));
    }

    private static async Task<Results<NoContent, NotFound>> UnmarkWatchedAsync(
        Guid episodeId, ClaimsPrincipal user, TrackingService tracking, DateTimeOffset? changedAt, CancellationToken ct) =>
        await tracking.UnmarkWatchedAsync(user.GetUserId(), episodeId, changedAt, ct) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static async Task<FollowDto> ToDtoAsync(Follow follow, TrackingContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var show = await db.Shows.AsNoTracking().SingleOrDefaultAsync(s => s.ShowId == follow.ShowId, ct);
        var aired = await db.Episodes.CountAsync(e => e.ShowId == follow.ShowId && e.SeasonNumber > 0 && e.AirsAtUtc <= now, ct);
        var watched = await db.WatchedEpisodes.CountAsync(w => w.UserId == follow.UserId && w.ShowId == follow.ShowId, ct);

        return new FollowDto(follow.ShowId, show?.Title, show?.PosterUrl, show?.Status, follow.Status, watched, aired, follow.FollowedAtUtc);
    }
}
