using Microsoft.EntityFrameworkCore;
using Tracking.Data;
using TvTracker.IntegrationEvents;
using TvTracker.UserPlans;

namespace Tracking.IntegrationTests;

/// <summary>
/// A service's copy of members' plans (<see cref="PlanCopyHandlers{TContext}"/>), which Tracking, Analytics, Social and
/// Recommendations all keep the same way: the rule on its own, and the handlers against Tracking's database.
/// </summary>
public sealed class PlanCopyTests(TrackingDatabase database) : IClassFixture<TrackingDatabase>
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string userId = $"user-{Guid.NewGuid():N}";

    [Fact]
    public void With_no_copy_a_plan_is_kept_unless_it_is_free_and_never_changed()
    {
        Assert.Equal((Plans.Plus, At), PlanCopyHandlers<TrackingContext>.Kept(null, Plans.Plus, At));
        // Back on free after paying: kept, so an older "plus" still on its way can't bring the plan back.
        Assert.Equal((Plans.Free, At), PlanCopyHandlers<TrackingContext>.Kept(null, Plans.Free, At));
        Assert.Null(PlanCopyHandlers<TrackingContext>.Kept(null, Plans.Free, DateTimeOffset.MinValue));
    }

    [Fact]
    public void The_newest_change_wins_over_a_copy()
    {
        var copy = (Plans.Plus, At);
        Assert.Equal((Plans.Family, At.AddHours(1)), PlanCopyHandlers<TrackingContext>.Kept(copy, Plans.Family, At.AddHours(1)));
        // The same moment again (the daily snapshot repeating the change) is taken.
        Assert.Equal((Plans.Plus, At), PlanCopyHandlers<TrackingContext>.Kept(copy, Plans.Plus, At));
        Assert.Null(PlanCopyHandlers<TrackingContext>.Kept(copy, Plans.Free, At.AddHours(-1)));
    }

    private async Task<string?> PlanAsync()
    {
        await using var db = database.Context();
        return (await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, Ct))?.Plan;
    }

    [Fact]
    public async Task A_change_is_kept_and_a_late_older_one_is_left()
    {
        await using (var db = database.Context())
        {
            await new PlanCopyHandlers<TrackingContext>(db).HandleAsync(new UserPlanChangedIntegrationEvent(userId, Plans.Plus, At), Ct);
        }
        await using (var db = database.Context())
        {
            await new PlanCopyHandlers<TrackingContext>(db).HandleAsync(new UserPlanChangedIntegrationEvent(userId, Plans.Free, At.AddHours(-2)), Ct);
        }

        Assert.Equal(Plans.Plus, await PlanAsync());
    }

    [Fact]
    public async Task The_daily_snapshot_heals_a_lost_change_and_one_without_a_plan_changes_nothing()
    {
        await using (var db = database.Context())
        {
            await new PlanCopyHandlers<TrackingContext>(db).HandleAsync(new UserProfileSnapshotIntegrationEvent(userId, "Sam"), Ct);
        }
        Assert.Null(await PlanAsync());

        await using (var db = database.Context())
        {
            await new PlanCopyHandlers<TrackingContext>(db).HandleAsync(new UserProfileSnapshotIntegrationEvent(userId, "Sam", Plans.Family, At), Ct);
        }
        Assert.Equal(Plans.Family, await PlanAsync());
    }

    [Fact]
    public async Task The_same_event_delivered_again_is_applied_once()
    {
        var change = new UserPlanChangedIntegrationEvent(userId, Plans.Plus, At);
        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var db = database.Context();
            await new PlanCopyHandlers<TrackingContext>(db).HandleAsync(change, Ct);
        }

        await using var read = database.Context();
        Assert.Equal(1, await read.ProcessedIntegrationEvents.CountAsync(p => p.EventId == change.Id, Ct));
        // Under the name each service's own handlers had, so events applied before the move stay applied.
        Assert.Equal("PlanHandlers.UserPlanChangedIntegrationEvent", (await read.ProcessedIntegrationEvents.SingleAsync(p => p.EventId == change.Id, Ct)).Handler);
    }
}
