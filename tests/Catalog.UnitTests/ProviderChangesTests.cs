using Catalog.SyncWorker;
using Catalog.SyncWorker.Providers;
using TvTracker.IntegrationEvents;

namespace Catalog.UnitTests;

/// <summary>Refreshing what TVmaze changed, and the slower schedule behind it (see CONTEXT.md, TVmaze budget).</summary>
public class ProviderChangesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid(), E = Guid.NewGuid();

    [Fact]
    public void A_show_changed_since_its_last_sync_is_due()
    {
        var changes = new Dictionary<string, DateTimeOffset>
        {
            ["1"] = Now.AddHours(-5),   // changed after a sync 10 hours ago: due
            ["2"] = Now.AddHours(-20),  // changed before a sync 10 hours ago: current
            ["3"] = Now.AddMinutes(-30), // changed half an hour ago: TVmaze's cache may still have the old copy, so next time
            ["4"] = Now.AddHours(-3),   // synced 2.5 hours ago, half an hour after the change: maybe a cached copy, so once more
            ["5"] = Now.AddHours(-3)    // never fetched: due
        };
        var known = new (Guid, string, DateTime?)[]
        {
            (A, "1", Now.AddHours(-10).UtcDateTime),
            (B, "2", Now.AddHours(-10).UtcDateTime),
            (C, "3", Now.AddHours(-10).UtcDateTime),
            (D, "4", Now.AddHours(-2.5).UtcDateTime),
            (E, "5", null),
            (Guid.NewGuid(), "6", Now.AddHours(-10).UtcDateTime) // not in the list: unchanged
        };

        Assert.Equal([A, D, E], ProviderChanges.Changed(changes, known, Now));
    }

    [Fact]
    public void A_sync_an_hour_after_the_change_is_trusted()
    {
        var changes = new Dictionary<string, DateTimeOffset> { ["1"] = Now.AddHours(-3) };

        Assert.Empty(ProviderChanges.Changed(changes, [(A, "1", Now.AddHours(-1.5).UtcDateTime)], Now));
    }

    [Theory]
    [InlineData(1.0, "day")]
    [InlineData(25.0, "week")]
    [InlineData(168.0, "week")]
    [InlineData(169.0, "month")]
    [InlineData(800.0, null)]
    public void The_shortest_update_list_that_covers_the_gap(double hours, string? since) =>
        Assert.Equal(since, TvMazeCatalogProvider.UpdatesSince(TimeSpan.FromHours(hours)));

    [Fact]
    public void Safety_net_schedule()
    {
        var now = Now.UtcDateTime;
        Assert.Equal(now.AddDays(30), CatalogSynchronizer.NextRefresh(ShowStatus.Ended, [Now.AddDays(-2)], now));
        Assert.Equal(now.AddDays(30), CatalogSynchronizer.NextRefresh(ShowStatus.Cancelled, [], now));
        // An episode in the last fortnight: its ratings are still coming in.
        Assert.Equal(now.AddDays(3), CatalogSynchronizer.NextRefresh(ShowStatus.Airing, [Now.AddDays(-40), Now.AddDays(-5), Now.AddDays(2)], now));
        Assert.Equal(now.AddDays(7), CatalogSynchronizer.NextRefresh(ShowStatus.Airing, [Now.AddDays(-40), Now.AddDays(2), null], now));
        Assert.Equal(now.AddDays(7), CatalogSynchronizer.NextRefresh(ShowStatus.Airing, [Now.AddDays(-15)], now));
    }
}
