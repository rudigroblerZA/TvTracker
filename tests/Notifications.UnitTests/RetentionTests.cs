using Notifications.API.Data;
using Notifications.API.Services;

namespace Notifications.UnitTests;

/// <summary>
/// What the daily trim keeps: read items for 90 days, unread ones for a year, finished deliveries for a month, and
/// pending deliveries whatever their age.
/// </summary>
public class RetentionTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private static InboxItem Item(int createdDaysAgo, int? readDaysAgo) => new()
    {
        UserId = "u",
        Title = "t",
        Body = "b",
        CreatedAtUtc = Now.AddDays(-createdDaysAgo),
        ReadAtUtc = readDaysAgo is { } days ? Now.AddDays(-days) : null
    };

    private static Delivery Delivery(DeliveryStatus status, int scheduledDaysAgo) =>
        new() { UserId = "u", Status = status, ScheduledForUtc = Now.AddDays(-scheduledDaysAgo) };

    [Fact]
    public void Read_items_go_after_90_days_and_unread_ones_after_a_year()
    {
        var expired = InboxRetention.ExpiredItems(Now).Compile();

        Assert.False(expired(Item(createdDaysAgo: 200, readDaysAgo: 89)));
        Assert.True(expired(Item(createdDaysAgo: 200, readDaysAgo: 91)));
        Assert.False(expired(Item(createdDaysAgo: 364, readDaysAgo: null)));
        Assert.True(expired(Item(createdDaysAgo: 366, readDaysAgo: null)));
        Assert.True(expired(Item(createdDaysAgo: 366, readDaysAgo: 1)));
    }

    [Fact]
    public void Finished_deliveries_go_after_30_days_and_pending_ones_stay()
    {
        var expired = InboxRetention.ExpiredDeliveries(Now).Compile();

        Assert.False(expired(Delivery(DeliveryStatus.Sent, 29)));
        Assert.True(expired(Delivery(DeliveryStatus.Sent, 31)));
        Assert.True(expired(Delivery(DeliveryStatus.Skipped, 31)));
        Assert.True(expired(Delivery(DeliveryStatus.Failed, 31)));
        Assert.False(expired(Delivery(DeliveryStatus.Pending, 400)));
    }
}
