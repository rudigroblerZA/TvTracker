using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
public sealed class IntegrationEventRetention(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<IntegrationEventRetention> logger)
    : BackgroundService
{
    public const int OutboxDays = 7;
    public const int InboxDays = 30;

    private const int Batch = 5000;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstRunDelay, time, stoppingToken);
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await TrimAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Trimming the outbox and inbox failed; trying again in an hour");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TrimAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var (outbox, inbox) = (0, 0);
        while (true)
        {
            // A fresh scope per batch, so the change tracker doesn't grow over a long first trim.
            await using var scope = scopeFactory.CreateAsyncScope();
            var (o, i) = await scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>()
                .TrimAsync(now.AddDays(-OutboxDays), now.AddDays(-InboxDays), Batch, ct);
            (outbox, inbox) = (outbox + o, inbox + i);
            if (o < Batch && i < Batch)
            {
                break;
            }
        }
        if (outbox + inbox > 0)
        {
            logger.LogInformation("Trimmed {Outbox} published outbox rows and {Inbox} inbox rows", outbox, inbox);
        }
    }
}
