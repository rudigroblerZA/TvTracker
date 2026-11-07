using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;

namespace Notifications.API.Services;

/// <summary>
/// Keeps inboxes and the delivery log bounded: daily, deletes inbox items read more than <see cref="ReadDays"/> days
/// ago or created more than <see cref="AnyDays"/> ago, and finished deliveries scheduled more than
/// <see cref="DeliveryDays"/> ago, in batches so no delete holds a long lock.
/// <list type="bullet">
/// <item><b>Why:</b> both were deleted only with the account, so a long-time follower's inbox grew without end.</item>
/// <item><b>Read, then gone:</b> 90 days after it was read an item has done its job; an unread one stays a year, in
/// case it's still to be seen. Deleting an item deletes its deliveries (cascade).</item>
/// <item><b>Deliveries:</b> a sent, skipped or failed row only says what happened; a month covers looking into it.
/// Pending ones are kept whatever their age: the delivery worker still owes them.</item>
/// <item><b>Nothing else reads further back:</b> the weekly digest reads the inbox since the last digest (a week), and
/// the fan-out's "already told" check is for redeliveries and crossovers re-sent for at most 14 days.</item>
/// </list>
/// </summary>
public sealed class InboxRetention(IServiceScopeFactory scopes, TimeProvider time, ILogger<InboxRetention> logger) : BackgroundService
{
    public const int ReadDays = 90;
    public const int AnyDays = 365;
    public const int DeliveryDays = 30;

    private const int Batch = 5000;
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Inbox items past keeping at <paramref name="nowUtc"/>.
    /// </summary>
    internal static Expression<Func<InboxItem, bool>> ExpiredItems(DateTime nowUtc)
    {
        var read = nowUtc.AddDays(-ReadDays);
        var any = nowUtc.AddDays(-AnyDays);
        return i => i.ReadAtUtc < read || i.CreatedAtUtc < any;
    }

    /// <summary>
    /// Finished deliveries past keeping at <paramref name="nowUtc"/>.
    /// </summary>
    internal static Expression<Func<Delivery, bool>> ExpiredDeliveries(DateTime nowUtc)
    {
        var before = nowUtc.AddDays(-DeliveryDays);
        return d => d.Status != DeliveryStatus.Pending && d.ScheduledForUtc < before;
    }

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
                logger.LogWarning(ex, "Trimming inboxes failed; trying again tomorrow");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TrimAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var (items, deliveries) = (0, 0);
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsContext>();
            var i = await db.Inbox.Where(ExpiredItems(now)).OrderBy(x => x.Id).Take(Batch).ExecuteDeleteAsync(ct);
            var d = await db.Deliveries.Where(ExpiredDeliveries(now)).OrderBy(x => x.Id).Take(Batch).ExecuteDeleteAsync(ct);
            (items, deliveries) = (items + i, deliveries + d);
            if (i < Batch && d < Batch)
            {
                break;
            }
        }
        if (items + deliveries > 0)
        {
            logger.LogInformation("Trimmed {Items} inbox items and {Deliveries} finished deliveries", items, deliveries);
        }
    }
}
