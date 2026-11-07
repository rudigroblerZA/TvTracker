using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TvTracker.Persistence;

/// <summary>
/// Keeps the outbox and inbox tables bounded: hourly, deletes published outbox rows older than <see cref="OutboxDays"/>
/// days and inbox rows older than <see cref="InboxDays"/>, in batches so no delete holds a long lock.
/// <list type="bullet">
/// <item><b>Outbox:</b> a published row is only kept for looking into what was sent; a week covers that.</item>
/// <item><b>Inbox:</b> a row is what makes a redelivered event a no-op, so it must outlive any redelivery. RabbitMQ
/// redelivers within minutes, and the daily snapshots and replays are new events with new ids; 30 days is far past
/// both. Notifications also uses it to send an alert once by a stable id: a crossover is re-sent hourly for at most
/// 14 days, a season verdict only within 2 days of being ready, both inside the window.</item>
/// </list>
/// Before this, nothing deleted from either: after two weeks of development use, Catalog's outbox held 8,645 rows
/// (45 MB) and Social's inbox 27,117.
/// </summary>
public sealed class IntegrationEventRetention(IServiceScopeFactory scopes, TimeProvider time, ILogger<IntegrationEventRetention> logger)
    : PeriodicTrim(scopes, time, logger)
{
    public const int OutboxDays = 7;
    public const int InboxDays = 30;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override TimeSpan FirstRunDelay => TimeSpan.FromMinutes(2);

    protected override async Task<IReadOnlyList<(string Rows, int Deleted)>> TrimBatchAsync(
        IServiceProvider services, DateTime nowUtc, int batch, CancellationToken ct)
    {
        var (outbox, inbox) = await services.GetRequiredService<IIntegrationEventOutbox>()
            .TrimAsync(nowUtc.AddDays(-OutboxDays), nowUtc.AddDays(-InboxDays), batch, ct);
        return [("published outbox rows", outbox), ("inbox rows", inbox)];
    }
}
