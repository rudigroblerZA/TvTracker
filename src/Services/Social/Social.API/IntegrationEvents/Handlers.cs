using Microsoft.EntityFrameworkCore;
using Social.API.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Social.API.IntegrationEvents;

/// <summary>Keeps show titles, posters and episode numbers from the catalog, for rendering the feed.</summary>
public sealed class CatalogEventHandlers(SocialContext db) :
    IIntegrationEventHandler<ShowAddedIntegrationEvent>,
    IIntegrationEventHandler<ShowUpdatedIntegrationEvent>,
    IIntegrationEventHandler<EpisodesScheduledIntegrationEvent>,
    IIntegrationEventHandler<EpisodeScheduledIntegrationEvent>
{
    public Task HandleAsync(ShowAddedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(CatalogEventHandlers), () => UpsertShowAsync(e.ShowId, e.Title, e.PosterUrl, ct), ct);

    public Task HandleAsync(ShowUpdatedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(CatalogEventHandlers), () => UpsertShowAsync(e.ShowId, e.Title, e.PosterUrl, ct), ct);

    public Task HandleAsync(EpisodesScheduledIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(CatalogEventHandlers), async () =>
        {
            var ids = e.Episodes.Select(x => x.EpisodeId).ToList();
            var existing = await db.Episodes.Where(x => ids.Contains(x.EpisodeId)).ToDictionaryAsync(x => x.EpisodeId, ct);
            foreach (var episode in e.Episodes)
            {
                Upsert(existing.GetValueOrDefault(episode.EpisodeId), e.ShowId, episode.EpisodeId, episode.SeasonNumber, episode.EpisodeNumber);
            }
        }, ct);

    public Task HandleAsync(EpisodeScheduledIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(CatalogEventHandlers), async () =>
            Upsert(await db.Episodes.FindAsync([e.EpisodeId], ct), e.ShowId, e.EpisodeId, e.SeasonNumber, e.EpisodeNumber), ct);

    private void Upsert(EpisodeCopy? episode, Guid showId, Guid episodeId, int season, int number)
    {
        if (episode is null)
        {
            db.Episodes.Add(new EpisodeCopy { EpisodeId = episodeId, ShowId = showId, SeasonNumber = season, EpisodeNumber = number });
            return;
        }
        episode.SeasonNumber = season;
        episode.EpisodeNumber = number;
    }

    private async Task UpsertShowAsync(Guid showId, string title, string? posterUrl, CancellationToken ct)
    {
        if (await db.Shows.FindAsync([showId], ct) is { } show)
        {
            show.Title = title;
            show.PosterUrl = posterUrl;
        }
        else
        {
            db.Shows.Add(new ShowCopy { ShowId = showId, Title = title, PosterUrl = posterUrl });
        }
    }
}

/// <summary>Keeps users' display names from Identity, and forgets a deleted user entirely.</summary>
public sealed class ProfileEventHandlers(SocialContext db, TimeProvider time) :
    IIntegrationEventHandler<UserRegisteredIntegrationEvent>,
    IIntegrationEventHandler<UserProfileUpdatedIntegrationEvent>,
    IIntegrationEventHandler<UserProfileSnapshotIntegrationEvent>,
    IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public Task HandleAsync(UserRegisteredIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(ProfileEventHandlers), () => SetNameAsync(e.UserId, e.DisplayName, ct), ct);

    public Task HandleAsync(UserProfileUpdatedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(ProfileEventHandlers), () => SetNameAsync(e.UserId, e.DisplayName, ct), ct);

    public Task HandleAsync(UserProfileSnapshotIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(ProfileEventHandlers), () => SetNameAsync(e.UserId, e.DisplayName, ct), ct);

    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        await db.Activities.Where(a => a.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Follows.Where(f => f.FollowerId == e.UserId || f.FolloweeId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Profiles.Where(p => p.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Names arrive in any order (a snapshot can follow an update), and the latest content wins either way.</summary>
    private async Task SetNameAsync(string userId, string displayName, CancellationToken ct)
    {
        var profile = await db.Profiles.FindAsync([userId], ct);
        if (profile is null)
        {
            db.Profiles.Add(new Profile { UserId = userId, DisplayName = displayName, UpdatedAtUtc = time.GetUtcNow().UtcDateTime });
        }
        else if (profile.DisplayName != displayName)
        {
            profile.DisplayName = displayName;
            profile.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        }
    }
}

/// <summary>
/// Turns users' tracking and rating events into feed items. Undoing something (unwatching, unfollowing, removing a
/// rating) takes it back out of the feed, so followers never see what the user took back.
/// </summary>
public sealed class ActivityEventHandlers(SocialContext db) :
    IIntegrationEventHandler<EpisodeWatchedIntegrationEvent>,
    IIntegrationEventHandler<EpisodeUnwatchedIntegrationEvent>,
    IIntegrationEventHandler<ShowCompletedIntegrationEvent>,
    IIntegrationEventHandler<ShowFollowedIntegrationEvent>,
    IIntegrationEventHandler<ShowUnfollowedIntegrationEvent>,
    IIntegrationEventHandler<ShowRatedIntegrationEvent>,
    IIntegrationEventHandler<ShowRatingRemovedIntegrationEvent>
{
    /// <summary>Episodes of one show watched within this of the last are added to the same item.</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromHours(6);

    /// <summary>Rating a show again within this replaces the rating in the feed rather than adding another.</summary>
    private static readonly TimeSpan RerateWindow = TimeSpan.FromDays(1);

    public Task HandleAsync(EpisodeWatchedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var at = (e.WatchedAtUtc ?? e.CreatedAt).UtcDateTime;
            var recent = await db.Activities
                .Where(a => a.UserId == e.UserId && a.ShowId == e.ShowId && a.Kind == ActivityKinds.Watched
                    && a.OccurredAtUtc >= at - SessionGap && a.OccurredAtUtc <= at + SessionGap)
                .OrderByDescending(a => a.OccurredAtUtc)
                .FirstOrDefaultAsync(ct);
            // Watching a show you don't follow follows it too; the watch says all that, so the follow isn't shown.
            await db.Activities
                .Where(a => a.UserId == e.UserId && a.ShowId == e.ShowId && a.Kind == ActivityKinds.Followed
                    && a.OccurredAtUtc >= at - SessionGap && a.OccurredAtUtc <= at + SessionGap)
                .ExecuteDeleteAsync(ct);

            if (recent is null)
            {
                db.Activities.Add(new Activity
                {
                    Id = Guid.CreateVersion7(), UserId = e.UserId, Kind = ActivityKinds.Watched, ShowId = e.ShowId,
                    EpisodeIds = e.EpisodeIds.Distinct().ToList(), OccurredAtUtc = at
                });
                return;
            }

            recent.EpisodeIds = recent.EpisodeIds.Union(e.EpisodeIds).ToList();
            recent.OccurredAtUtc = at > recent.OccurredAtUtc ? at : recent.OccurredAtUtc;
        });

    public Task HandleAsync(EpisodeUnwatchedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var items = await db.Activities
                .Where(a => a.UserId == e.UserId && a.ShowId == e.ShowId && a.Kind == ActivityKinds.Watched && a.EpisodeIds.Contains(e.EpisodeId))
                .ToListAsync(ct);
            foreach (var item in items)
            {
                item.EpisodeIds = item.EpisodeIds.Where(id => id != e.EpisodeId).ToList();
                if (item.EpisodeIds.Count == 0)
                {
                    db.Activities.Remove(item);
                }
            }
        });

    public Task HandleAsync(ShowCompletedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () =>
        {
            Add(e.UserId, ActivityKinds.Finished, e.ShowId, e.CreatedAt);
            return Task.CompletedTask;
        });

    /// <summary>A follow that comes with a watch of the same show (watching follows it) is left to the watch.</summary>
    public Task HandleAsync(ShowFollowedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var at = e.CreatedAt.UtcDateTime;
            var watched = await db.Activities.AnyAsync(a => a.UserId == e.UserId && a.ShowId == e.ShowId && a.Kind == ActivityKinds.Watched
                && a.OccurredAtUtc >= at - SessionGap && a.OccurredAtUtc <= at + SessionGap, ct);
            if (!watched)
            {
                Add(e.UserId, ActivityKinds.Followed, e.ShowId, e.CreatedAt);
            }
        });

    public Task HandleAsync(ShowUnfollowedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () =>
            db.Activities.Where(a => a.UserId == e.UserId && a.ShowId == e.ShowId && a.Kind == ActivityKinds.Followed).ExecuteDeleteAsync(ct));

    public Task HandleAsync(ShowRatedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var since = e.CreatedAt.UtcDateTime - RerateWindow;
            if (await db.Activities.FirstOrDefaultAsync(
                    a => a.UserId == e.UserId && a.ShowId == e.ShowId && a.Kind == ActivityKinds.Rated && a.OccurredAtUtc >= since, ct) is { } recent)
            {
                recent.Stars = e.Stars;
                recent.OccurredAtUtc = e.CreatedAt.UtcDateTime;
                return;
            }
            Add(e.UserId, ActivityKinds.Rated, e.ShowId, e.CreatedAt, e.Stars);
        });

    public Task HandleAsync(ShowRatingRemovedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () =>
            db.Activities.Where(a => a.UserId == e.UserId && a.ShowId == e.ShowId && a.Kind == ActivityKinds.Rated).ExecuteDeleteAsync(ct));

    private void Add(string userId, string kind, Guid showId, DateTimeOffset at, int? stars = null) =>
        db.Activities.Add(new Activity { Id = Guid.CreateVersion7(), UserId = userId, Kind = kind, ShowId = showId, Stars = stars, OccurredAtUtc = at.UtcDateTime });

    private Task HandleOnceAsync(IntegrationEvent e, CancellationToken ct, Func<Task> apply) =>
        db.HandleOnceAsync(e, nameof(ActivityEventHandlers), apply, ct);
}

internal static class InboxExtensions
{
    /// <summary>The inbox: applies an event once per handler class, then saves the change and the mark together.</summary>
    public static async Task HandleOnceAsync(this SocialContext db, IntegrationEvent e, string handlerClass, Func<Task> apply, CancellationToken ct)
    {
        var handler = $"{handlerClass}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        await apply();
        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}
