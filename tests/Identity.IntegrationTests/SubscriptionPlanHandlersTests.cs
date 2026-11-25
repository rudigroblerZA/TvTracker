using Identity.API.IntegrationEvents;
using Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;
using TvTracker.Tests;

namespace Identity.IntegrationTests;

/// <summary>
/// The member's plan as Financials' subscription events move it: what Identity is left with, and what it tells the
/// services that keep a copy of the plan.
/// </summary>
public sealed class SubscriptionPlanHandlersTests(IdentityDatabase database) : IClassFixture<IdentityDatabase>
{
    private static readonly DateTimeOffset At = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly RecordingEventBus bus = new();
    private readonly string userId = $"user-{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_subscription_starting_moves_the_plan_and_says_so()
    {
        await ProfileAsync();

        await HandleAsync(new SubscriptionStartedIntegrationEvent(userId, Plans.Plus, At));

        Assert.Equal(Plans.Plus, await PlanAsync());
        Assert.Equal(Plans.Plus, Assert.Single(PlanChanges()).Plan);
    }

    [Fact]
    public async Task The_same_event_again_changes_nothing()
    {
        await ProfileAsync();
        var started = new SubscriptionStartedIntegrationEvent(userId, Plans.Plus, At);
        await HandleAsync(started);
        await SetPlanAsync(Plans.Family);
        bus.Clear();

        await HandleAsync(started);

        Assert.Equal(Plans.Family, await PlanAsync());
        Assert.Empty(PlanChanges());
    }

    [Fact]
    public async Task An_ending_moves_the_plan_back_to_free()
    {
        await ProfileAsync(Plans.Plus);

        await HandleAsync(new SubscriptionEndedIntegrationEvent(userId, Plans.Plus, At));

        Assert.Equal(Plans.Free, await PlanAsync());
        Assert.Equal(Plans.Free, Assert.Single(PlanChanges()).Plan);
    }

    [Fact]
    public async Task An_ending_leaves_a_plan_someone_changed_in_between()
    {
        // An admin's change: no longer the subscription's plan.
        await ProfileAsync(Plans.Family);

        await HandleAsync(new SubscriptionEndedIntegrationEvent(userId, Plans.Plus, At));

        Assert.Equal(Plans.Family, await PlanAsync());
        Assert.Empty(PlanChanges());
    }

    [Fact]
    public async Task A_late_ending_doesnt_undo_a_newer_subscription()
    {
        await ProfileAsync();
        // The first subscription ended at 09:00, but that event is held up; the member has subscribed again since.
        await HandleAsync(new SubscriptionStartedIntegrationEvent(userId, Plans.Plus, At.AddHours(1)));
        bus.Clear();

        await HandleAsync(new SubscriptionEndedIntegrationEvent(userId, Plans.Plus, At));

        Assert.Equal(Plans.Plus, await PlanAsync());
        Assert.Empty(PlanChanges());
    }

    [Fact]
    public async Task A_late_start_doesnt_bring_back_a_plan_that_ended_since()
    {
        await ProfileAsync();
        await HandleAsync(new SubscriptionStartedIntegrationEvent(userId, Plans.Plus, At.AddMinutes(-10)));
        await HandleAsync(new SubscriptionEndedIntegrationEvent(userId, Plans.Plus, At.AddHours(1)));
        bus.Clear();

        // A redelivered copy under a new id, from before the ending.
        await HandleAsync(new SubscriptionStartedIntegrationEvent(userId, Plans.Plus, At));

        Assert.Equal(Plans.Free, await PlanAsync());
        Assert.Empty(PlanChanges());
    }

    [Fact]
    public async Task An_event_for_an_account_since_deleted_is_taken_and_changes_nothing()
    {
        await HandleAsync(new SubscriptionStartedIntegrationEvent(userId, Plans.Plus, At));

        await using var db = database.Context();
        Assert.False(await db.Profiles.AnyAsync(p => p.UserId == userId, Ct));
        Assert.Empty(PlanChanges());
    }

    private async Task HandleAsync(IntegrationEvent e)
    {
        await using var db = database.Context();
        var outbox = new IntegrationEventOutbox<IdentityContext>(db, bus, NullLogger<IntegrationEventOutbox<IdentityContext>>.Instance);
        var handlers = new SubscriptionPlanHandlers(db, outbox, NullLogger<SubscriptionPlanHandlers>.Instance);
        await (e switch
        {
            SubscriptionStartedIntegrationEvent started => handlers.HandleAsync(started, Ct),
            SubscriptionEndedIntegrationEvent ended => handlers.HandleAsync(ended, Ct),
            _ => throw new ArgumentException("Not a subscription event.", nameof(e))
        });
    }

    private async Task ProfileAsync(string plan = Plans.Free)
    {
        await using var db = database.Context();
        db.Profiles.Add(new UserProfile
        {
            UserId = userId, DisplayName = "Tess", Plan = plan, EffectivePlan = plan, CreatedAtUtc = At.UtcDateTime, UpdatedAtUtc = At.UtcDateTime
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetPlanAsync(string plan)
    {
        await using var db = database.Context();
        (await db.Profiles.SingleAsync(p => p.UserId == userId, Ct)).Plan = plan;
        await db.SaveChangesAsync(Ct);
    }

    private async Task<string> PlanAsync()
    {
        await using var db = database.Context();
        return (await db.Profiles.SingleAsync(p => p.UserId == userId, Ct)).Plan;
    }

    private IEnumerable<UserPlanChangedIntegrationEvent> PlanChanges() => bus.Of<UserPlanChangedIntegrationEvent>().Where(e => e.UserId == userId);
}
