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
/// <item><b>Ended:</b> the plan goes back to free only while it's still that subscription's plan: an admin's change in
/// between stands, and so does a newer subscription (the events say which subscription they are about).</item>
/// <item><b>Once:</b> each event is recorded with its change (the inbox), so a redelivery changes nothing.</item>
/// <item><b>In order:</b> an event from before the plan last changed is left alone, so a late "ended" can't undo a
/// newer subscription (or an admin's change made since); and a start that arrives after its own subscription's ending
/// is left too.</item>
/// </list>
/// A member without a profile has nothing to move: a checkout makes the profile first, so this is an account deleted
/// since.
/// </summary>
public sealed class SubscriptionPlanHandlers(IdentityContext db, IIntegrationEventOutbox outbox, ILogger<SubscriptionPlanHandlers> logger)
    : IIntegrationEventHandler<SubscriptionStartedIntegrationEvent>, IIntegrationEventHandler<SubscriptionEndedIntegrationEvent>
{
    public Task HandleAsync(SubscriptionStartedIntegrationEvent e, CancellationToken ct) =>
        ApplyAsync(e, e.UserId, async profile =>
        {
            var at = e.StartedAtUtc.UtcDateTime;
            if (e.SubscriptionId is { } id && id == profile.EndedSubscriptionId && at < profile.EndedSubscriptionAtUtc)
            {
                // Its own ending got here first: this subscription is already over.
                logger.LogInformation("The start of subscription {Subscription} for {UserId} was left: it has ended since", id, e.UserId);
            }
            else if (Older(profile, e.StartedAtUtc))
            {
                return;
            }
            else if (await PlanState.SetPaidPlanAsync(db, outbox, profile, e.Plan, e.StartedAtUtc, ct))
            {
                profile.PlanSubscriptionId = e.SubscriptionId;
                logger.LogInformation("Plan of {UserId} set to {Plan} by their subscription", e.UserId, profile.Plan);
            }
            else
            {
                // Nothing to move (subscribing again to the plan they still have), but it is the newest word on their
                // plan, and the subscription now paying for it: an older event still on its way mustn't undo it.
                profile.PlanSubscriptionId = e.SubscriptionId;
                profile.PlanChangedAtUtc = at;
            }
        }, ct);

    public Task HandleAsync(SubscriptionEndedIntegrationEvent e, CancellationToken ct) =>
        ApplyAsync(e, e.UserId, async profile =>
        {
            var at = e.EndedAtUtc.UtcDateTime;
            if (e.SubscriptionId is { } id && (profile.EndedSubscriptionAtUtc is not { } known || at >= known))
            {
                (profile.EndedSubscriptionId, profile.EndedSubscriptionAtUtc) = (id, at);
            }
            // Theirs to lose only while the plan is still this subscription's: an admin's change in between stands, and
            // so does a newer subscription to the same plan. Without ids on either side, the plan's name has to do.
            var paysForTheirPlan = profile.Plan == e.Plan
                && (e.SubscriptionId is null || profile.PlanSubscriptionId is null || profile.PlanSubscriptionId == e.SubscriptionId);
            if (paysForTheirPlan && !Older(profile, e.EndedAtUtc) && await PlanState.SetPaidPlanAsync(db, outbox, profile, Plans.Free, e.EndedAtUtc, ct))
            {
                logger.LogInformation("Plan of {UserId} set to {Plan}: their subscription ended", e.UserId, profile.Plan);
            }
        }, ct);

    /// <summary>
    /// Whether the plan has moved since <paramref name="at"/>: events can arrive late and out of order (one that waited
    /// in the outbox, a redelivery), and one from before the last change has nothing to say.
    /// </summary>
    private bool Older(UserProfile profile, DateTimeOffset at)
    {
        if (profile.PlanChangedAtUtc is not { } changed || at.UtcDateTime >= changed)
        {
            return false;
        }
        logger.LogInformation("A subscription event of {At:u} for {UserId} was left: their plan changed after it, at {Changed:u}", at, profile.UserId, changed);
        return true;
    }

    private async Task ApplyAsync(IntegrationEvent e, string userId, Func<UserProfile, Task> apply, CancellationToken ct)
    {
        await db.HandleOnceAsync(e, InboxExtensions.HandlerName<SubscriptionPlanHandlers>(e), async () =>
        {
            // An account since deleted has no plan to move; the event is still marked.
            if (await db.Profiles.FindAsync([userId], ct) is { } profile)
            {
                await apply(profile);
            }
        }, ct);
        await outbox.PublishAddedAsync(ct);
    }
}
