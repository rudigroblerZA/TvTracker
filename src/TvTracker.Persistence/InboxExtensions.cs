using Microsoft.EntityFrameworkCore;
using TvTracker.EventBus;

namespace TvTracker.Persistence;

/// <summary>
/// Idempotent consumers. A handler makes its change through <see cref="HandleOnceAsync"/>, which skips an event the
/// handler has already applied and otherwise applies it and marks it, together.
/// </summary>
public static class InboxExtensions
{
    /// <summary>
    /// Applies an event once per handler: skips it when <paramref name="handler"/> has already processed it, else runs
    /// <paramref name="apply"/>, marks the event processed and saves.
    /// <list type="bullet">
    /// <item><b>All or nothing:</b> the whole of that runs in one transaction through the execution strategy, so a
    /// handler that deletes with <c>ExecuteDelete</c> before adding rows can't be left half done by a failure at the
    /// save: either the event is applied and marked, or nothing changed and the bus's retry finds things as they were.</item>
    /// <item><b>What <paramref name="apply"/> may do:</b> database work only: change tracked entities (it needn't save),
    /// and run <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>. A call to another system belongs outside it, where it
    /// doesn't hold the transaction open or get repeated with it. It can run more than once (a transient failure retries it), and the
    /// change tracker is cleared before each attempt, so it loads what it changes itself and doesn't rely on anything
    /// tracked before the call.</item>
    /// <item><b>Outbox:</b> events <paramref name="apply"/> adds to the outbox are stored with the change; publish them
    /// after this returns.</item>
    /// </list>
    /// </summary>
    /// <param name="handler">The inbox's name for this handler (and event, where one class handles several). It keys
    /// the processed rows, so it is fixed: renaming it lets a redelivery of an event already applied be applied again.</param>
    public static Task HandleOnceAsync<TContext>(this TContext db, IntegrationEvent e, string handler, Func<Task> apply, CancellationToken ct)
        where TContext : DbContext, IIntegrationEventLogDbContext =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.HasProcessedAsync(e, handler, ct))
            {
                return;
            }
            await apply();
            db.MarkProcessed(e, handler);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });

    /// <summary>
    /// The inbox name for a class that handles several events: the class and the event type.
    /// </summary>
    public static string HandlerName<THandler>(IntegrationEvent e) => HandlerName(typeof(THandler).Name, e);

    /// <summary>
    /// The same under a name given outright, for a handler whose class no longer has the name its stored rows carry.
    /// </summary>
    public static string HandlerName(string handlerClass, IntegrationEvent e) => $"{handlerClass}.{e.GetType().Name}";

    /// <summary>
    /// Whether <paramref name="handler"/> has already processed the event. For a handler that can't use
    /// <see cref="HandleOnceAsync"/> (one that marks only some of the time); the rest go through that.
    /// </summary>
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
