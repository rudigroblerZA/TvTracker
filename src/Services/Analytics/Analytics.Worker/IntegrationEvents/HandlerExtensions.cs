using Analytics.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.EventBus;
using TvTracker.Persistence;

namespace Analytics.Worker.IntegrationEvents;

/// <summary>
/// The inbox pattern every handler follows (see CONTEXT.md, "Consuming"): skip an event already processed, apply it,
/// mark it processed, save. Here the whole of that runs in one transaction through the execution strategy, so a
/// handler that deletes with <c>ExecuteDelete</c> before adding rows (a show's cast) can't be left half done by a
/// transient failure at the save: either the event is applied and marked, or nothing changed and RabbitMQ's retry
/// finds the copy as it was. The change tracker is cleared at the start of each attempt, since a retry would
/// otherwise re-add the first attempt's rows.
/// </summary>
public static class HandlerExtensions
{
    /// <param name="handler">The inbox's name for this handler and event; fixed, since it keys the processed rows.</param>
    public static Task HandleOnceAsync(this AnalyticsContext db, IntegrationEvent e, string handler, Func<Task> apply, CancellationToken ct) =>
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

    /// <summary>The usual inbox name: the handler class and the event type.</summary>
    public static string HandlerName<THandler>(IntegrationEvent e) => $"{typeof(THandler).Name}.{e.GetType().Name}";
}
