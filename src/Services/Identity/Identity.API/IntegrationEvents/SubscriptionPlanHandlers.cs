using Identity.API.Services;
using Identity.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Identity.API.IntegrationEvents;

/// <summary>
/// Moves a member's plan when Financials.API says they started or stopped paying for one.
/// <list type="bullet">
/// <item><b>Started:</b> the plan becomes the subscription's.</item>
/// <item><b>Ended:</b> the plan goes back to free only while it's still the subscription's plan: an admin's change in
/// between stands.</item>
/// <item><b>Once:</b> each event is recorded with its change (the inbox), so a redelivery changes nothing.</item>
/// <item><b>In order:</b> an event from before the plan last changed is left alone, so a late "ended" can't undo a
/// newer subscription (or an admin's change made since).</item>
/// </list>
/// A member without a profile has nothing to move: a checkout makes the profile first, so this is an account deleted
/// since.
/// </summary>
public sealed class SubscriptionPlanHandlers(IdentityContext db, IIntegrationEventOutbox outbox, ILogger<SubscriptionPlanHandlers> logger)
    : IIntegrationEventHandler<SubscriptionStartedIntegrationEvent>, IIntegrationEventHandler<SubscriptionEndedIntegrationEvent>
{
    public Task HandleAsync(SubscriptionStartedIntegrationEvent e, CancellationToken ct) =>
        ApplyAsync(e, e.UserId, _ => e.Plan, e.StartedAtUtc, ct);

    public Task HandleAsync(SubscriptionEndedIntegrationEvent e, CancellationToken ct) =>
        ApplyAsync(e, e.UserId, current => current == e.Plan ? Plans.Free : current, e.EndedAtUtc, ct);

    private async Task ApplyAsync(IntegrationEvent e, string userId, Func<string, string> plan, DateTimeOffset at, CancellationToken ct)
    {
        var handler = $"{nameof(SubscriptionPlanHandlers)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }
        if (await db.Profiles.FindAsync([userId], ct) is { } profile)
        {
            if (profile.PlanChangedAtUtc is { } changed && at.UtcDateTime < changed)
            {
                // Events can arrive late and out of order (one that waited in the outbox, a redelivery): the plan has
                // moved since this happened, so it has nothing to say.
                logger.LogInformation("A subscription event of {At:u} for {UserId} was left: their plan changed after it, at {Changed:u}", at, userId, changed);
            }
            else if (await PlanState.SetPaidPlanAsync(db, outbox, profile, plan(profile.Plan), at, ct))
            {
                logger.LogInformation("Plan of {UserId} set to {Plan} by their subscription", userId, profile.Plan);
            }
            else
            {
                // Nothing to move (subscribing again to the plan they still have), but it is the newest word on their
                // plan: an older event still on its way mustn't undo it.
                profile.PlanChangedAtUtc = at.UtcDateTime;
            }
        }
        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
    }
}
