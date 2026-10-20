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

    public Task HandleAsync(EpisodeScheduledIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            // The episode may arrive before its show (for example after a missed ShowAdded).
            if (await db.Shows.FindAsync([e.ShowId], ct) is null)
            {
                db.Shows.Add(new ShowCopy { ShowId = e.ShowId, Title = e.ShowTitle, UpdatedAtUtc = time.GetUtcNow().UtcDateTime });
            }

            var episode = await db.Episodes.FindAsync([e.EpisodeId], ct);
            if (episode is null)
            {
                episode = new EpisodeCopy { EpisodeId = e.EpisodeId, ShowId = e.ShowId, Title = e.Title };
                db.Episodes.Add(episode);
            }

            episode.SeasonNumber = e.SeasonNumber;
            episode.EpisodeNumber = e.EpisodeNumber;
            episode.Title = e.Title;
            episode.AirsAtUtc = e.AirsAtUtc;
            episode.RuntimeMinutes = e.RuntimeMinutes;
        });

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
    }
}
