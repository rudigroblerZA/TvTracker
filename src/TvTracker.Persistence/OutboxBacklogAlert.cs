using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TvTracker;

namespace TvTracker.Persistence;

/// <summary>
/// Fires when an event saved to this service's outbox has gone unpublished for <see cref="After"/>: RabbitMQ is down or
/// refusing it, or an event can't be sent at all. <see cref="OutboxPublisher"/> retries every 15 seconds, so a few
/// minutes is already far past a hiccup, and every service downstream is missing that change meanwhile. Every service
/// with an outbox gets it (<c>AddIntegrationEventOutbox</c>).
/// </summary>
public sealed class OutboxBacklogAlert<TContext>(TimeProvider time) : IAlertCheck
    where TContext : DbContext, IIntegrationEventLogDbContext
{
    public static readonly TimeSpan After = TimeSpan.FromMinutes(5);

    public string Name => "outbox-backlog";

    public async Task<AlertState> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<TContext>();
        var pending = await db.IntegrationEventLogs.AsNoTracking()
            .Where(e => e.State != EventState.Published)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Oldest = g.Min(e => e.CreatedAtUtc) })
            .SingleOrDefaultAsync(ct);
        return Evaluate(pending?.Count ?? 0, pending?.Oldest, time.GetUtcNow().UtcDateTime);
    }

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
