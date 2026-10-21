using Catalog.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Catalog.SyncWorker;

/// <summary>Stores Analytics' show tags on the shows, for search's tag filter.</summary>
public sealed class ShowTagsChangedHandler(CatalogContext db) : IIntegrationEventHandler<ShowTagsChangedIntegrationEvent>
{
    public async Task HandleAsync(ShowTagsChangedIntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        if (await db.HasProcessedAsync(@event, nameof(ShowTagsChangedHandler), cancellationToken))
        {
            return;
        }

        var tagsById = @event.Shows.DistinctBy(s => s.ShowId).ToDictionary(s => s.ShowId, s => s.Tags);
        var ids = tagsById.Keys.ToList();
        var shows = await db.Shows.Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        foreach (var show in shows)
        {
            var tags = tagsById[show.Id].ToList();
            if (!show.Tags.SequenceEqual(tags))
            {
                show.Tags = tags;
            }
        }

        db.MarkProcessed(@event, nameof(ShowTagsChangedHandler));
        await db.SaveChangesAsync(cancellationToken);
    }
}
