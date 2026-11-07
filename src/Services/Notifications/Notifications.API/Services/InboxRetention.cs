using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;
using TvTracker.Persistence;

namespace Notifications.API.Services;

/// <summary>
/// Keeps inboxes and the delivery log bounded: daily, deletes inbox items read more than <see cref="ReadDays"/> days
/// ago or created more than <see cref="AnyDays"/> ago, and finished deliveries scheduled more than
/// <see cref="DeliveryDays"/> ago (the loop and batches are <see cref="PeriodicTrim"/>'s).
/// <list type="bullet">
/// <item><b>Why:</b> both were deleted only with the account, so a long-time follower's inbox grew without end.</item>
/// <item><b>Read, then gone:</b> 90 days after it was read an item has done its job; an unread one stays a year, in
/// case it's still to be seen. Deleting an item deletes its deliveries (cascade).</item>
/// <item><b>Pending is kept, whatever its age:</b> the delivery worker still owes a pending delivery, so neither it nor
/// the item it belongs to is deleted until it's sent, skipped or failed.</item>
/// <item><b>Deliveries:</b> a finished row only says what happened; a month covers looking into it.</item>
/// <item><b>One index per rule:</b> each age rule is its own delete, on its own index (migration
/// <c>InboxRetentionIndexes</c>), so a batch doesn't scan the whole inbox.</item>
/// <item><b>Nothing else reads further back:</b> the weekly digest reads the inbox since the last digest (a week), and
/// the fan-out's "already told" check is for redeliveries and crossovers re-sent for at most 14 days.</item>
/// </list>
/// </summary>
public sealed class InboxRetention(IServiceScopeFactory scopes, TimeProvider time, ILogger<InboxRetention> logger)
    : PeriodicTrim(scopes, time, logger)
{
    public const int ReadDays = 90;
    public const int AnyDays = 365;
    public const int DeliveryDays = 30;

    protected override TimeSpan Interval => TimeSpan.FromDays(1);

    protected override TimeSpan FirstRunDelay => TimeSpan.FromMinutes(5);

    /// <summary>
    /// Items read long enough ago.
    /// </summary>
    internal static Expression<Func<InboxItem, bool>> ReadLongAgo(DateTime nowUtc)
    {
        var before = nowUtc.AddDays(-ReadDays);
        return i => i.ReadAtUtc < before;
    }

    /// <summary>
    /// Items kept a year, read or not.
    /// </summary>
    internal static Expression<Func<InboxItem, bool>> CreatedLongAgo(DateTime nowUtc)
    {
        var before = nowUtc.AddDays(-AnyDays);
        return i => i.CreatedAtUtc < before;
    }

    /// <summary>
    /// Finished deliveries past keeping.
    /// </summary>
    internal static Expression<Func<Delivery, bool>> ExpiredDeliveries(DateTime nowUtc)
    {
        var before = nowUtc.AddDays(-DeliveryDays);
        return d => d.Status != DeliveryStatus.Pending && d.ScheduledForUtc < before;
    }

    protected override async Task<IReadOnlyList<(string Rows, int Deleted)>> TrimBatchAsync(
        IServiceProvider services, DateTime nowUtc, int batch, CancellationToken ct)
    {
        var db = services.GetRequiredService<NotificationsContext>();
        var owed = db.Deliveries.Where(d => d.Status == DeliveryStatus.Pending);
        var read = await db.Inbox.Where(ReadLongAgo(nowUtc)).Where(i => !owed.Any(d => d.InboxItemId == i.Id))
            .OrderBy(i => i.ReadAtUtc).Take(batch).ExecuteDeleteAsync(ct);
        var old = await db.Inbox.Where(CreatedLongAgo(nowUtc)).Where(i => !owed.Any(d => d.InboxItemId == i.Id))
            .OrderBy(i => i.CreatedAtUtc).Take(batch).ExecuteDeleteAsync(ct);
        var deliveries = await db.Deliveries.Where(ExpiredDeliveries(nowUtc))
            .OrderBy(d => d.ScheduledForUtc).Take(batch).ExecuteDeleteAsync(ct);
        return [("inbox items read long ago", read), ("year-old inbox items", old), ("finished deliveries", deliveries)];
    }
}
