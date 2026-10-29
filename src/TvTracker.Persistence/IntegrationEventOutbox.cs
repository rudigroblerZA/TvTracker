using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TvTracker.EventBus;

namespace TvTracker.Persistence;

public interface IIntegrationEventOutbox
{
    /// <summary>
    /// Adds the event to the service's DbContext. It is written by the next SaveChanges,
    /// in the same transaction as the data change.
    /// </summary>
    void Add(IntegrationEvent @event);

    /// <summary>Publishes the events added through this instance. Call after SaveChanges.</summary>
    Task PublishAddedAsync(CancellationToken cancellationToken = default);

    /// <summary>Publishes any stored events that haven't been published yet (used by the background publisher).</summary>
    Task PublishPendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes up to <paramref name="batch"/> published outbox rows created before <paramref name="publishedBefore"/>,
    /// and as many inbox rows processed before <paramref name="processedBefore"/> (used by <see cref="IntegrationEventRetention"/>).
    /// </summary>
    Task<(int Outbox, int Inbox)> TrimAsync(DateTime publishedBefore, DateTime processedBefore, int batch, CancellationToken cancellationToken = default);
}

public sealed class IntegrationEventOutbox<TContext>(
    TContext dbContext,
    IEventBus eventBus,
    ILogger<IntegrationEventOutbox<TContext>> logger) : IIntegrationEventOutbox
    where TContext : DbContext, IIntegrationEventLogDbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly List<Guid> _added = [];

    public void Add(IntegrationEvent @event)
    {
        var type = @event.GetType();
        dbContext.IntegrationEventLogs.Add(new IntegrationEventLogEntry
        {
            EventId = @event.Id,
            EventTypeName = type.AssemblyQualifiedName!,
            Content = JsonSerializer.Serialize(@event, type, JsonOptions),
            CreatedAtUtc = DateTime.UtcNow,
            State = EventState.NotPublished
        });
        _added.Add(@event.Id);
    }

    public async Task PublishAddedAsync(CancellationToken cancellationToken = default)
    {
        if (_added.Count == 0)
        {
            return;
        }

        var ids = _added.ToList();
        _added.Clear();

        var entries = await dbContext.IntegrationEventLogs
            .Where(e => ids.Contains(e.EventId) && e.State != EventState.Published)
            .ToListAsync(cancellationToken);

        // Publish in the order the events were added (e.g. ShowAdded before its EpisodeScheduled events).
        entries = entries.OrderBy(e => ids.IndexOf(e.EventId)).ToList();

        await PublishEntriesAsync(entries, cancellationToken);
    }

    public async Task PublishPendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;

        var entries = await dbContext.IntegrationEventLogs
            .Where(e => e.State != EventState.Published && e.CreatedAtUtc < cutoff)
            .OrderBy(e => e.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        await PublishEntriesAsync(entries, cancellationToken);
    }

    public async Task<(int Outbox, int Inbox)> TrimAsync(DateTime publishedBefore, DateTime processedBefore, int batch,
        CancellationToken cancellationToken = default)
    {
        // Unpublished and failed rows are kept whatever their age: the publisher still owes them.
        var outbox = await dbContext.IntegrationEventLogs
            .Where(e => e.State == EventState.Published && e.CreatedAtUtc < publishedBefore)
            .OrderBy(e => e.CreatedAtUtc)
            .Take(batch)
            .ExecuteDeleteAsync(cancellationToken);
        var inbox = await dbContext.ProcessedIntegrationEvents
            .Where(e => e.ProcessedAtUtc < processedBefore)
            .OrderBy(e => e.ProcessedAtUtc)
            .Take(batch)
            .ExecuteDeleteAsync(cancellationToken);
        return (outbox, inbox);
    }

    private async Task PublishEntriesAsync(List<IntegrationEventLogEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            try
            {
                var type = Type.GetType(entry.EventTypeName)
                    ?? throw new InvalidOperationException($"Unknown event type {entry.EventTypeName}");
                var @event = (IntegrationEvent)JsonSerializer.Deserialize(entry.Content, type, JsonOptions)!;

                await eventBus.PublishAsync(@event, cancellationToken);
                entry.State = EventState.Published;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Publishing event {EventId} failed; it will be retried", entry.EventId);
                entry.State = EventState.PublishFailed;
            }

            entry.TimesSent++;
        }

        if (entries.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
