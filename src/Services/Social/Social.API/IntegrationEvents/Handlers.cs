using Microsoft.EntityFrameworkCore;
using Social.API.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Social.API.IntegrationEvents;

/// <summary>
/// Keeps show titles, posters, episode numbers, air times and runtimes from the catalog, for the feed, comparing progress
/// and planning an evening for two.
/// </summary>
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
                Upsert(existing.GetValueOrDefault(episode.EpisodeId), e.ShowId, episode.EpisodeId, episode.SeasonNumber, episode.EpisodeNumber, episode.AirsAtUtc, episode.RuntimeMinutes);
            }
        }, ct);

    public Task HandleAsync(EpisodeScheduledIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(CatalogEventHandlers), async () =>
            Upsert(await db.Episodes.FindAsync([e.EpisodeId], ct), e.ShowId, e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.AirsAtUtc, e.RuntimeMinutes), ct);

    private void Upsert(EpisodeCopy? episode, Guid showId, Guid episodeId, int season, int number, DateTimeOffset? airsAtUtc, int? runtimeMinutes)
    {
        if (episode is null)
        {
            db.Episodes.Add(new EpisodeCopy { EpisodeId = episodeId, ShowId = showId, SeasonNumber = season, EpisodeNumber = number, AirsAtUtc = airsAtUtc, RuntimeMinutes = runtimeMinutes });
            return;
        }
        episode.SeasonNumber = season;
        episode.EpisodeNumber = number;
        episode.AirsAtUtc = airsAtUtc;
        episode.RuntimeMinutes = runtimeMinutes;
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
        await db.Blocks.Where(b => b.BlockerId == e.UserId || b.BlockedId == e.UserId).ExecuteDeleteAsync(ct);
        await db.UserShows.Where(s => s.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Watches.Where(w => w.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.SharedLists.Where(l => l.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Profiles.Where(p => p.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Plans.Where(p => p.UserId == e.UserId).ExecuteDeleteAsync(ct);
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

/// <summary>
/// Keeps each user's followed shows, ratings and watched episodes: what "people you follow" on a show page and "watching
/// together" read. Tracking's and Ratings' daily snapshots fill it for users from before Social, and repair it after a
/// lost event, as Analytics does: anything recorded after the snapshot was taken is kept.
/// </summary>
public sealed class UserStateHandlers(SocialContext db) :
    IIntegrationEventHandler<EpisodeWatchedIntegrationEvent>,
    IIntegrationEventHandler<EpisodeUnwatchedIntegrationEvent>,
    IIntegrationEventHandler<ShowFollowedIntegrationEvent>,
    IIntegrationEventHandler<ShowUnfollowedIntegrationEvent>,
    IIntegrationEventHandler<ShowRatedIntegrationEvent>,
    IIntegrationEventHandler<ShowRatingRemovedIntegrationEvent>,
    IIntegrationEventHandler<TrackingSnapshotIntegrationEvent>,
    IIntegrationEventHandler<RatingsSnapshotIntegrationEvent>
{
    public Task HandleAsync(EpisodeWatchedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(UserStateHandlers), async () =>
        {
            var ids = e.EpisodeIds.Distinct().ToList();
            var existing = await db.Watches.Where(w => w.UserId == e.UserId && ids.Contains(w.EpisodeId)).Select(w => w.EpisodeId).ToListAsync(ct);
            db.Watches.AddRange(ids.Except(existing).Select(id => new UserWatch
            {
                UserId = e.UserId, EpisodeId = id, ShowId = e.ShowId, WatchedAtUtc = (e.WatchedAtUtc ?? e.CreatedAt).UtcDateTime
            }));
        }, ct);

    public Task HandleAsync(EpisodeUnwatchedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(UserStateHandlers), () =>
            db.Watches.Where(w => w.UserId == e.UserId && w.EpisodeId == e.EpisodeId).ExecuteDeleteAsync(ct), ct);

    public Task HandleAsync(ShowFollowedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(UserStateHandlers), async () =>
        {
            var show = await ShowAsync(e.UserId, e.ShowId, ct);
            show.Followed = true;
            show.FollowedAtUtc ??= e.CreatedAt.UtcDateTime;
        }, ct);

    public Task HandleAsync(ShowUnfollowedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(UserStateHandlers), async () =>
        {
            var show = await ShowAsync(e.UserId, e.ShowId, ct);
            show.Followed = false;
            show.FollowedAtUtc = null;
        }, ct);

    public Task HandleAsync(ShowRatedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(UserStateHandlers), async () => { (await ShowAsync(e.UserId, e.ShowId, ct)).Stars = e.Stars; }, ct);

    public Task HandleAsync(ShowRatingRemovedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(UserStateHandlers), async () => { (await ShowAsync(e.UserId, e.ShowId, ct)).Stars = null; }, ct);

    public Task HandleAsync(TrackingSnapshotIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(UserStateHandlers), async () =>
        {
            var takenAt = e.TakenAtUtc.UtcDateTime;
            var watches = await db.Watches.Where(w => w.UserId == e.UserId).ToListAsync(ct);
            var watched = watches.Select(w => w.EpisodeId).ToHashSet();
            var snapshot = e.Watches.DistinctBy(w => w.EpisodeId).ToDictionary(w => w.EpisodeId);
            db.Watches.AddRange(snapshot.Values.Where(w => !watched.Contains(w.EpisodeId)).Select(w => new UserWatch
            {
                UserId = e.UserId, EpisodeId = w.EpisodeId, ShowId = w.ShowId, WatchedAtUtc = w.WatchedAtUtc.UtcDateTime
            }));
            db.Watches.RemoveRange(watches.Where(w => !snapshot.ContainsKey(w.EpisodeId) && w.WatchedAtUtc < takenAt));

            var shows = await db.UserShows.Where(s => s.UserId == e.UserId).ToDictionaryAsync(s => s.ShowId, ct);
            var follows = e.Follows.DistinctBy(f => f.ShowId).ToDictionary(f => f.ShowId);
            foreach (var follow in follows.Values)
            {
                if (!shows.TryGetValue(follow.ShowId, out var show))
                {
                    db.UserShows.Add(show = new UserShow { UserId = e.UserId, ShowId = follow.ShowId });
                    shows[follow.ShowId] = show;
                }
                show.Followed = true;
                show.FollowedAtUtc ??= follow.FollowedAtUtc.UtcDateTime;
            }
            foreach (var show in shows.Values.Where(s => s.Followed && !follows.ContainsKey(s.ShowId) && s.FollowedAtUtc < takenAt))
            {
                show.Followed = false;
                show.FollowedAtUtc = null;
            }
        }, ct);

    /// <summary>Ratings carry no time here, so a rating changed after the snapshot can be undone until the next one.</summary>
    public Task HandleAsync(RatingsSnapshotIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(UserStateHandlers), async () =>
        {
            var shows = await db.UserShows.Where(s => s.UserId == e.UserId).ToDictionaryAsync(s => s.ShowId, ct);
            var ratings = e.Shows.DistinctBy(r => r.ShowId).ToDictionary(r => r.ShowId, r => r.Stars);
            foreach (var (showId, stars) in ratings)
            {
                if (!shows.TryGetValue(showId, out var show))
                {
                    db.UserShows.Add(show = new UserShow { UserId = e.UserId, ShowId = showId });
                    shows[showId] = show;
                }
                show.Stars = stars;
            }
            foreach (var show in shows.Values.Where(s => s.Stars is not null && !ratings.ContainsKey(s.ShowId)))
            {
                show.Stars = null;
            }
        }, ct);

    private async Task<UserShow> ShowAsync(string userId, Guid showId, CancellationToken ct)
    {
        if (await db.UserShows.FindAsync([userId, showId], ct) is { } show)
        {
            return show;
        }
        show = new UserShow { UserId = userId, ShowId = showId };
        db.UserShows.Add(show);
        return show;
    }
}

/// <summary>
/// Keeps copies of the lists users share, from Tracking. An update older than the copy is ignored, since events can
/// arrive out of order; Tracking's daily snapshot repairs a copy that missed one.
/// </summary>
public sealed class SharedListHandlers(SocialContext db) :
    IIntegrationEventHandler<ListSharedIntegrationEvent>,
    IIntegrationEventHandler<ListUnsharedIntegrationEvent>,
    IIntegrationEventHandler<TrackingSnapshotIntegrationEvent>
{
    public Task HandleAsync(ListSharedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(SharedListHandlers), async () =>
            Apply(await db.SharedLists.FindAsync([e.ListId], ct), e.ListId, e.UserId, e.Name, e.Description, e.ShowIds, e.UpdatedAtUtc), ct);

    public Task HandleAsync(ListUnsharedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(SharedListHandlers), () =>
            db.SharedLists.Where(l => l.ListId == e.ListId && l.UpdatedAtUtc <= e.UpdatedAtUtc.UtcDateTime).ExecuteDeleteAsync(ct), ct);

    /// <summary>Snapshots from before list sharing carry no lists (null), and change nothing.</summary>
    public Task HandleAsync(TrackingSnapshotIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(SharedListHandlers), async () =>
        {
            if (e.SharedLists is not { } lists)
            {
                return;
            }

            var copies = await db.SharedLists.Where(l => l.UserId == e.UserId).ToDictionaryAsync(l => l.ListId, ct);
            foreach (var list in lists)
            {
                Apply(copies.GetValueOrDefault(list.ListId), list.ListId, e.UserId, list.Name, list.Description, list.ShowIds, list.UpdatedAtUtc);
            }
            var shared = lists.Select(l => l.ListId).ToHashSet();
            db.SharedLists.RemoveRange(copies.Values.Where(l => !shared.Contains(l.ListId) && l.UpdatedAtUtc < e.TakenAtUtc.UtcDateTime));
        }, ct);

    private void Apply(SharedList? copy, Guid listId, string userId, string name, string? description, IReadOnlyList<Guid> showIds, DateTimeOffset updatedAt)
    {
        if (copy is null)
        {
            db.SharedLists.Add(new SharedList
            {
                ListId = listId, UserId = userId, Name = name, Description = description, ShowIds = showIds.ToList(), UpdatedAtUtc = updatedAt.UtcDateTime
            });
            return;
        }
        if (copy.UpdatedAtUtc > updatedAt.UtcDateTime)
        {
            return;
        }
        copy.Name = name;
        copy.Description = description;
        copy.ShowIds = showIds.ToList();
        copy.UpdatedAtUtc = updatedAt.UtcDateTime;
    }
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

/// <summary>
/// Keeps <see cref="UserPlanCopy"/> from Identity's plan changes and daily profile snapshot (which heals a lost change), as
/// Tracking does; the newest by Identity's change time wins.
/// </summary>
public sealed class PlanHandlers(SocialContext db)
    : IIntegrationEventHandler<UserPlanChangedIntegrationEvent>, IIntegrationEventHandler<UserProfileSnapshotIntegrationEvent>
{
    public Task HandleAsync(UserPlanChangedIntegrationEvent e, CancellationToken ct) =>
        ApplyAsync(e, e.UserId, e.Plan, e.ChangedAtUtc, ct);

    // A snapshot from before plans existed carries none; a plan never changed is free since sign-up, as old as can be.
    public Task HandleAsync(UserProfileSnapshotIntegrationEvent e, CancellationToken ct) =>
        e.Plan is { } plan ? ApplyAsync(e, e.UserId, plan, e.PlanChangedAtUtc ?? DateTimeOffset.MinValue, ct) : Task.CompletedTask;

    private async Task ApplyAsync(IntegrationEvent e, string userId, string plan, DateTimeOffset changedAt, CancellationToken ct)
    {
        var handler = $"{nameof(PlanHandlers)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var copy = await db.Plans.FindAsync([userId], ct);
        if (copy is null)
        {
            // Free with no change is what no row already means.
            if (plan != TvTracker.IntegrationEvents.Plans.Free || changedAt != DateTimeOffset.MinValue)
            {
                db.Plans.Add(new UserPlanCopy { UserId = userId, Plan = plan, ChangedAtUtc = changedAt });
            }
        }
        else if (changedAt >= copy.ChangedAtUtc)
        {
            copy.Plan = plan;
            copy.ChangedAtUtc = changedAt;
        }

        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}
