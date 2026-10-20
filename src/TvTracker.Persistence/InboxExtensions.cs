using Microsoft.EntityFrameworkCore;
using TvTracker.EventBus;

namespace TvTracker.Persistence;

/// <summary>
/// Idempotent consumers: a handler checks <see cref="HasProcessedAsync"/> first, makes its changes,
/// calls <see cref="MarkProcessed"/>, then saves everything in one SaveChanges.
/// </summary>
public static class InboxExtensions
{
    public static Task<bool> HasProcessedAsync(this IIntegrationEventLogDbContext db, IntegrationEvent @event, string handler, CancellationToken cancellationToken = default) =>
        db.ProcessedIntegrationEvents.AnyAsync(p => p.EventId == @event.Id && p.Handler == handler, cancellationToken);

    public static void MarkProcessed(this IIntegrationEventLogDbContext db, IntegrationEvent @event, string handler) =>
        db.ProcessedIntegrationEvents.Add(new ProcessedIntegrationEvent
        {
            EventId = @event.Id,
            Handler = handler,
            ProcessedAtUtc = DateTime.UtcNow
        });
}
