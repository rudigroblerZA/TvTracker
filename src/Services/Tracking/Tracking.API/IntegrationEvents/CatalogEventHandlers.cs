using Microsoft.EntityFrameworkCore;
using Tracking.API.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Tracking.API.IntegrationEvents;

/// <summary>Keeps Tracking's read copies of shows and episodes current.</summary>
public sealed class CatalogEventHandlers(TrackingContext db, TimeProvider time) :
    IIntegrationEventHandler<ShowAddedIntegrationEvent>,
    IIntegrationEventHandler<ShowUpdatedIntegrationEvent>,
    IIntegrationEventHandler<ShowStatusChangedIntegrationEvent>,
    IIntegrationEventHandler<EpisodesScheduledIntegrationEvent>,
    IIntegrationEventHandler<EpisodeScheduledIntegrationEvent>
{
    public Task HandleAsync(ShowAddedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () => UpsertShowAsync(e.ShowId, e.Title, e.PosterUrl, e.Status, e.Genres, ct));

    public Task HandleAsync(ShowUpdatedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () => UpsertShowAsync(e.ShowId, e.Title, e.PosterUrl, e.Status, e.Genres, ct));

    public Task HandleAsync(ShowStatusChangedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var show = await db.Shows.FindAsync([e.ShowId], ct);
            if (show is not null)
            {
                show.Status = e.NewStatus;
                show.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            }
        });

    public Task HandleAsync(EpisodesScheduledIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () => UpsertEpisodesAsync(e.ShowId, e.ShowTitle, e.Episodes, ct));

    /// <summary>Per-episode event published before batching; handled so messages still pending at upgrade apply.</summary>
    public Task HandleAsync(EpisodeScheduledIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () => UpsertEpisodesAsync(e.ShowId, e.ShowTitle,
            [new ScheduledEpisode(e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirsAtUtc, e.RuntimeMinutes)], ct));

    private async Task UpsertEpisodesAsync(Guid showId, string showTitle, IReadOnlyList<ScheduledEpisode> episodes, CancellationToken ct)
    {
        // The episodes may arrive before their show (for example after a missed ShowAdded).
        if (await db.Shows.FindAsync([showId], ct) is null)
        {
            db.Shows.Add(new ShowCopy { ShowId = showId, Title = showTitle, UpdatedAtUtc = time.GetUtcNow().UtcDateTime });
        }

        // One query for the whole batch instead of one per episode.
        var ids = episodes.Select(e => e.EpisodeId).ToList();
        var existing = await db.Episodes.Where(e => ids.Contains(e.EpisodeId)).ToDictionaryAsync(e => e.EpisodeId, ct);

        foreach (var e in episodes)
        {
            if (!existing.TryGetValue(e.EpisodeId, out var episode))
            {
                episode = new EpisodeCopy { EpisodeId = e.EpisodeId, ShowId = showId, Title = e.Title };
                db.Episodes.Add(episode);
                existing[e.EpisodeId] = episode;
            }

            episode.SeasonNumber = e.SeasonNumber;
            episode.EpisodeNumber = e.EpisodeNumber;
            episode.Title = e.Title;
            episode.AirsAtUtc = e.AirsAtUtc;
            episode.RuntimeMinutes = e.RuntimeMinutes;
        }
    }

    private async Task UpsertShowAsync(Guid showId, string title, string? posterUrl, ShowStatus status, IReadOnlyList<string> genres, CancellationToken ct)
    {
        var show = await db.Shows.FindAsync([showId], ct);
        if (show is null)
        {
            show = new ShowCopy { ShowId = showId, Title = title };
            db.Shows.Add(show);
        }

        show.Title = title;
        show.PosterUrl = posterUrl;
        show.Status = status;
        show.Genres = genres.ToList();
        show.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
    }

    private async Task HandleOnceAsync(IntegrationEvent e, CancellationToken ct, Func<Task> apply)
    {
        var handler = $"{nameof(CatalogEventHandlers)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        await apply();
        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Deletes everything Tracking holds for a deleted user.</summary>
public sealed class UserDeletedHandler(TrackingContext db) : IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        await db.WatchedEpisodes.Where(w => w.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Follows.Where(f => f.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Lists.Where(l => l.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Plans.Where(p => p.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// Keeps <see cref="UserPlanCopy"/> from Identity's plan changes and daily profile snapshot (which heals a lost change).
/// The newest by Identity's change time wins.
/// </summary>
public sealed class PlanHandlers(TrackingContext db)
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
