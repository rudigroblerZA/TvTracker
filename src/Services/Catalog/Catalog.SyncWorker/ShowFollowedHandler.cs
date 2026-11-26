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
    public Task HandleAsync(ShowFollowedIntegrationEvent @event, CancellationToken cancellationToken = default) =>
        db.HandleOnceAsync(@event, nameof(ShowFollowedHandler), () => synchronizer.MarkForRefreshAsync(@event.ShowId, cancellationToken), cancellationToken);
}
