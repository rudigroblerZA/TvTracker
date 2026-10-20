using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;

namespace Notifications.API.Services;

/// <summary>
/// Sends queued deliveries whose time has come. Failed sends are retried with backoff, up to
/// <see cref="MaxAttempts"/> times.
/// </summary>
public sealed class DeliveryWorker(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<DeliveryWorker> logger) : BackgroundService
{
    private const int MaxAttempts = 4;
    private const int BatchSize = 100;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                int processed;
                do
                {
                    processed = await ProcessBatchAsync(stoppingToken);
                }
                while (processed == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Delivery batch failed");
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsContext>();
        var senders = scope.ServiceProvider.GetServices<IChannelSender>().ToDictionary(s => s.Channel);
        var now = time.GetUtcNow().UtcDateTime;

        var due = await db.Deliveries
            .Include(d => d.InboxItem)
            .Where(d => d.Status == DeliveryStatus.Pending && d.ScheduledForUtc <= now)
            .OrderBy(d => d.ScheduledForUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var delivery in due)
        {
            delivery.Attempts++;

            if (!senders.TryGetValue(delivery.Channel, out var sender))
            {
                delivery.Status = DeliveryStatus.Skipped;
                delivery.LastError = "No sender for this channel.";
                continue;
            }

            SendResult result;
            try
            {
                result = await sender.SendAsync(delivery, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new SendResult(false, ex.Message);
            }

            if (result.Success)
            {
                delivery.Status = DeliveryStatus.Sent;
                delivery.SentAtUtc = now;
                delivery.LastError = null;
            }
            else if (delivery.Attempts >= MaxAttempts)
            {
                delivery.Status = DeliveryStatus.Failed;
                delivery.LastError = Truncate(result.Error);
                logger.LogWarning("Delivery {DeliveryId} ({Channel}) failed permanently: {Error}", delivery.Id, delivery.Channel, result.Error);
            }
            else
            {
                // Retry after 1, 4, 9 minutes.
                delivery.ScheduledForUtc = now.AddMinutes(delivery.Attempts * delivery.Attempts);
                delivery.LastError = Truncate(result.Error);
            }
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    private static string? Truncate(string? s) => s is { Length: > 1000 } ? s[..1000] : s;
}
