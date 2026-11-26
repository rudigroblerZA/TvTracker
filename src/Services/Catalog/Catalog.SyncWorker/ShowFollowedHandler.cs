using Catalog.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Catalog.SyncWorker;

/// <summary>
/// When someone follows a show, refresh it soon so its schedule is current.
/// </summary>
public sealed class ShowFollowedHandler(CatalogContext db, CatalogSynchronizer synchronizer) : IIntegrationEventHandler<ShowFollowedIntegrationEvent>
{
    public async Task HandleAsync(ShowFollowedIntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        await db.HandleOnceAsync(@event, nameof(ShowFollowedHandler), async () =>
        {
            await synchronizer.MarkForRefreshAsync(@event.ShowId, cancellationToken);
        }, cancellationToken);
    }
}
