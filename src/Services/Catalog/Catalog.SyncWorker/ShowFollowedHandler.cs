using Catalog.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Catalog.SyncWorker;

/// <summary>When someone follows a show, refresh it soon so its schedule is current.</summary>
public sealed class ShowFollowedHandler(CatalogContext db, CatalogSynchronizer synchronizer) : IIntegrationEventHandler<ShowFollowedIntegrationEvent>
{
    public async Task HandleAsync(ShowFollowedIntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        if (await db.HasProcessedAsync(@event, nameof(ShowFollowedHandler), cancellationToken))
        {
            return;
        }

        await synchronizer.MarkForRefreshAsync(@event.ShowId, cancellationToken);

        db.MarkProcessed(@event, nameof(ShowFollowedHandler));
        await db.SaveChangesAsync(cancellationToken);
    }
}
