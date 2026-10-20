using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TvTracker.Persistence;

/// <summary>
/// Retries events that were saved but not published (for example, RabbitMQ was down, or the process
/// stopped between SaveChanges and publishing).
/// </summary>
public sealed class OutboxPublisher(IServiceScopeFactory scopeFactory, ILogger<OutboxPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();
                await outbox.PublishPendingAsync(Grace, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Outbox publishing failed");
            }
        }
    }
}
