using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Ratings.API.IntegrationEvents;

/// <summary>Records watched episodes so comments can stay hidden until the reader has watched.</summary>
public sealed class EpisodeWatchedHandler(RatingsContext db) : IIntegrationEventHandler<EpisodeWatchedIntegrationEvent>
{
    public async Task HandleAsync(EpisodeWatchedIntegrationEvent e, CancellationToken ct)
    {
        if (await db.HasProcessedAsync(e, nameof(EpisodeWatchedHandler), ct))
        {
            return;
        }

        var ids = e.EpisodeIds.ToList();
        var existing = await db.WatchedEpisodes
            .Where(w => w.UserId == e.UserId && ids.Contains(w.EpisodeId))
            .Select(w => w.EpisodeId)
            .ToListAsync(ct);

        db.WatchedEpisodes.AddRange(ids.Except(existing).Select(id => new WatchedEpisodeCopy { UserId = e.UserId, EpisodeId = id }));
        db.MarkProcessed(e, nameof(EpisodeWatchedHandler));
        await db.SaveChangesAsync(ct);
    }
}

public sealed class UserDeletedHandler(RatingsContext db) : IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        await db.ShowRatings.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.EpisodeRatings.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Comments.Where(c => c.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Reactions.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.WatchedEpisodes.Where(w => w.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }
}
