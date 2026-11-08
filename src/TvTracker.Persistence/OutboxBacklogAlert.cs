using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TvTracker.Persistence;

/// <summary>
/// Fires when an event saved to this service's outbox has gone unpublished for <see cref="OutboxBacklog.After"/>:
/// RabbitMQ is down or refusing it, or an event can't be sent at all. <see cref="OutboxPublisher"/> retries every 15
/// seconds, so a few minutes is already far past a hiccup, and every service downstream is missing that change meanwhile.
/// Every service with an outbox gets it (<c>AddIntegrationEventOutbox</c>).
/// </summary>
public sealed class OutboxBacklogAlert<TContext>(IServiceScopeFactory scopes, TimeProvider time) : IAlertCheck
    where TContext : DbContext, IIntegrationEventLogDbContext
{
    public string Name => "outbox-backlog";

    public async Task<AlertState> CheckAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        // Unpublished as the two states, not "not published": Postgres can't seek an index on <>, and published rows
        // (kept 7 days) are nearly all of the table.
        var unpublished = scope.ServiceProvider.GetRequiredService<TContext>().IntegrationEventLogs.AsNoTracking()
            .Where(e => e.State == EventState.NotPublished || e.State == EventState.PublishFailed);
        var count = await unpublished.CountAsync(ct);
        var oldest = count == 0 ? null : await unpublished.MinAsync(e => (DateTime?)e.CreatedAtUtc, ct);
        return OutboxBacklog.Evaluate(count, oldest, time.GetUtcNow().UtcDateTime);
    }
}

/// <summary>
/// The outbox alert's rule, apart from any one context.
/// </summary>
public static class OutboxBacklog
{
    public static readonly TimeSpan After = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Firing once the oldest unpublished event is <see cref="After"/> old; younger ones are the publisher's normal work.
    /// </summary>
    internal static AlertState Evaluate(int unpublished, DateTime? oldestUtc, DateTime nowUtc)
    {
        if (oldestUtc is not { } oldest)
        {
            return new AlertState(false, "Every event is published");
        }
        var age = nowUtc - oldest;
        return new AlertState(age >= After, $"{unpublished} event{(unpublished == 1 ? "" : "s")} unpublished, the oldest {Minutes(age)}");
    }

    private static string Minutes(TimeSpan age) => age.TotalMinutes < 1 ? "under a minute old" : $"{(int)age.TotalMinutes} min old";
}
