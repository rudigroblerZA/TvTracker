using Microsoft.EntityFrameworkCore;
using Tracking.API.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Tracking.API.Services;

/// <summary>Write operations on follows and watched episodes, with their events.</summary>
public sealed class TrackingService(TrackingContext db, IIntegrationEventOutbox outbox, UserCache cache, TimeProvider time)
{
    public async Task<Follow> FollowAsync(string userId, Guid showId, WatchStatus status, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var follow = await db.Follows.FindAsync([userId, showId], ct);

        if (follow is null)
        {
            follow = new Follow { UserId = userId, ShowId = showId, Status = status, FollowedAtUtc = now, UpdatedAtUtc = now };
            db.Follows.Add(follow);
            outbox.Add(new ShowFollowedIntegrationEvent(userId, showId));
        }
        else if (follow.Status != status)
        {
            follow.Status = status;
            follow.UpdatedAtUtc = now;
        }

        await SaveAsync(userId, ct);
        return follow;
    }

    public async Task<bool> UnfollowAsync(string userId, Guid showId, CancellationToken ct)
    {
        var follow = await db.Follows.FindAsync([userId, showId], ct);
        if (follow is null)
        {
            return false;
        }

        db.Follows.Remove(follow);
        outbox.Add(new ShowUnfollowedIntegrationEvent(userId, showId));
        await SaveAsync(userId, ct);
        return true;
    }

    public async Task<Follow?> SetStatusAsync(string userId, Guid showId, WatchStatus status, CancellationToken ct)
    {
        var follow = await db.Follows.FindAsync([userId, showId], ct);
        if (follow is null)
        {
            return null;
        }

        var wasCompleted = follow.Status == WatchStatus.Completed;
        follow.Status = status;
        follow.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        if (status == WatchStatus.Completed && !wasCompleted)
        {
            outbox.Add(new ShowCompletedIntegrationEvent(userId, showId));
        }

        await SaveAsync(userId, ct);
        return follow;
    }

    /// <summary>
    /// Marks episodes watched in one batch per show and publishes one EpisodeWatched per show.
    /// Watching an episode of a show you don't follow starts following it.
    /// When every aired episode of an ended or cancelled show is watched, the show is marked Completed.
    /// </summary>
    public async Task<List<Guid>> MarkWatchedAsync(string userId, IReadOnlyCollection<EpisodeCopy> episodes, DateTimeOffset? watchedAt, CancellationToken ct)
    {
        if (episodes.Count == 0)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var at = (watchedAt is { } w && w <= now ? w : now).UtcDateTime;
        var episodeIds = episodes.Select(e => e.EpisodeId).ToList();

        var alreadyWatched = await db.WatchedEpisodes
            .Where(w => w.UserId == userId && episodeIds.Contains(w.EpisodeId))
            .Select(w => w.EpisodeId)
            .ToListAsync(ct);

        var added = new List<Guid>();

        foreach (var showGroup in episodes.Where(e => !alreadyWatched.Contains(e.EpisodeId)).GroupBy(e => e.ShowId))
        {
            var showId = showGroup.Key;
            var newIds = showGroup.Select(e => e.EpisodeId).ToList();

            db.WatchedEpisodes.AddRange(newIds.Select(id => new WatchedEpisode
            {
                UserId = userId,
                EpisodeId = id,
                ShowId = showId,
                WatchedAtUtc = at
            }));
            added.AddRange(newIds);

            var follow = await db.Follows.FindAsync([userId, showId], ct);
            if (follow is null)
            {
                follow = new Follow
                {
                    UserId = userId,
                    ShowId = showId,
                    Status = WatchStatus.Watching,
                    FollowedAtUtc = at,
                    UpdatedAtUtc = at
                };
                db.Follows.Add(follow);
                outbox.Add(new ShowFollowedIntegrationEvent(userId, showId));
            }
            else if (follow.Status is WatchStatus.PlanToWatch or WatchStatus.Paused)
            {
                follow.Status = WatchStatus.Watching;
                follow.UpdatedAtUtc = at;
            }

            outbox.Add(new EpisodeWatchedIntegrationEvent(userId, showId, newIds, at));

            if (follow.Status != WatchStatus.Completed && await IsFinishedAsync(userId, showId, newIds, now, ct))
            {
                follow.Status = WatchStatus.Completed;
                follow.UpdatedAtUtc = at;
                outbox.Add(new ShowCompletedIntegrationEvent(userId, showId));
            }
        }

        await SaveAsync(userId, ct);
        return added;
    }

    /// <summary>
    /// Marks episodes watched at their own times, for Bring your history: each watch keeps the date the export gives it, so
    /// Year in TV, the TV clock and streaks read the user's past as it happened. Episodes already watched are left as they
    /// are (a second import adds nothing), a show watched for the first time is followed from its earliest watch, and the
    /// events carry each watch's time, as the phone's offline replay does. Returns how many watches were added.
    /// </summary>
    public async Task<int> ImportWatchesAsync(string userId, IReadOnlyList<(Guid EpisodeId, DateTimeOffset WatchedAtUtc)> watches, CancellationToken ct)
    {
        if (watches.Count == 0)
        {
            return 0;
        }
        var now = time.GetUtcNow();
        var ids = watches.Select(w => w.EpisodeId).Distinct().ToList();
        var episodes = await db.Episodes.Where(e => ids.Contains(e.EpisodeId)).ToDictionaryAsync(e => e.EpisodeId, ct);
        var alreadyWatched = (await db.WatchedEpisodes.Where(w => w.UserId == userId && ids.Contains(w.EpisodeId)).Select(w => w.EpisodeId).ToListAsync(ct)).ToHashSet();
        // The earliest time given for an episode is its watch; a time in the future is now.
        var wanted = watches
            .Where(w => episodes.ContainsKey(w.EpisodeId) && !alreadyWatched.Contains(w.EpisodeId))
            .GroupBy(w => w.EpisodeId)
            .Select(g => (Episode: episodes[g.Key], At: g.Min(w => w.WatchedAtUtc) is var at && at <= now ? at.UtcDateTime : now.UtcDateTime))
            .ToList();

        var added = 0;
        foreach (var showGroup in wanted.GroupBy(w => w.Episode.ShowId))
        {
            var showId = showGroup.Key;
            var earliest = showGroup.Min(w => w.At);
            db.WatchedEpisodes.AddRange(showGroup.Select(w => new WatchedEpisode { UserId = userId, EpisodeId = w.Episode.EpisodeId, ShowId = showId, WatchedAtUtc = w.At }));
            added += showGroup.Count();

            var follow = await db.Follows.FindAsync([userId, showId], ct);
            if (follow is null)
            {
                follow = new Follow { UserId = userId, ShowId = showId, Status = WatchStatus.Watching, FollowedAtUtc = earliest, UpdatedAtUtc = earliest };
                db.Follows.Add(follow);
                outbox.Add(new ShowFollowedIntegrationEvent(userId, showId));
            }
            else if (follow.Status is WatchStatus.PlanToWatch or WatchStatus.Paused)
            {
                follow.Status = WatchStatus.Watching;
                follow.UpdatedAtUtc = now.UtcDateTime;
            }

            // One event per moment, so consumers keep each watch's own time.
            foreach (var moment in showGroup.GroupBy(w => w.At))
            {
                outbox.Add(new EpisodeWatchedIntegrationEvent(userId, showId, moment.Select(w => w.Episode.EpisodeId).ToList(), new DateTimeOffset(moment.Key, TimeSpan.Zero)));
            }

            var newIds = showGroup.Select(w => w.Episode.EpisodeId).ToList();
            if (follow.Status != WatchStatus.Completed && await IsFinishedAsync(userId, showId, newIds, now, ct))
            {
                follow.Status = WatchStatus.Completed;
                follow.UpdatedAtUtc = showGroup.Max(w => w.At);
                outbox.Add(new ShowCompletedIntegrationEvent(userId, showId));
            }
        }

        await SaveAsync(userId, ct);
        return added;
    }

    /// <summary>
    /// Removes a watched mark. <paramref name="changedAt"/> is when the user made the change (offline replays);
    /// a mark made after that time is kept, so the most recent change wins.
    /// </summary>
    public async Task<bool> UnmarkWatchedAsync(string userId, Guid episodeId, DateTimeOffset? changedAt, CancellationToken ct)
    {
        var watched = await db.WatchedEpisodes.FindAsync([userId, episodeId], ct);
        if (watched is null)
        {
            return false;
        }

        if (changedAt is { } t && watched.WatchedAtUtc > t.UtcDateTime)
        {
            return false;
        }

        db.WatchedEpisodes.Remove(watched);
        outbox.Add(new EpisodeUnwatchedIntegrationEvent(userId, watched.ShowId, episodeId));

        var follow = await db.Follows.FindAsync([userId, watched.ShowId], ct);
        if (follow?.Status == WatchStatus.Completed)
        {
            follow.Status = WatchStatus.Watching;
            follow.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        }

        await SaveAsync(userId, ct);
        return true;
    }

    private async Task<bool> IsFinishedAsync(string userId, Guid showId, List<Guid> justWatched, DateTimeOffset now, CancellationToken ct)
    {
        var show = await db.Shows.FindAsync([showId], ct);
        if (show is null || show.Status is not (ShowStatus.Ended or ShowStatus.Cancelled))
        {
            return false;
        }

        var airedIds = await db.Episodes
            .Where(e => e.ShowId == showId && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
            .Select(e => e.EpisodeId)
            .ToListAsync(ct);

        var watchedIds = await db.WatchedEpisodes
            .Where(w => w.UserId == userId && w.ShowId == showId)
            .Select(w => w.EpisodeId)
            .ToListAsync(ct);

        var all = watchedIds.Concat(justWatched).ToHashSet();
        return airedIds.Count > 0 && airedIds.All(all.Contains);
    }

    private async Task SaveAsync(string userId, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        await cache.InvalidateAsync(userId, ct);
    }
}
