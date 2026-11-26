using Microsoft.EntityFrameworkCore;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace TvTracker.UserPlans;

/// <summary>
/// Keeps a service's <see cref="UserPlanCopy"/> from Identity's plan changes and its daily profile snapshot (which heals a
/// lost change). The newest by Identity's change time wins (<see cref="Kept"/>).
/// </summary>
public sealed class PlanCopyHandlers<TContext>(TContext db)
    : IIntegrationEventHandler<UserPlanChangedIntegrationEvent>, IIntegrationEventHandler<UserProfileSnapshotIntegrationEvent>
    where TContext : DbContext, IIntegrationEventLogDbContext, IUserPlanCopies
{
    /// <summary>
    /// The inbox's name for these handlers: what each service's own class was called, which the processed rows carry.
    /// </summary>
    private const string InboxName = "PlanHandlers";

    public Task HandleAsync(UserPlanChangedIntegrationEvent e, CancellationToken ct) =>
        ApplyAsync(e, e.UserId, e.Plan, e.ChangedAtUtc, ct);

    // A snapshot from before plans existed carries none; a plan never changed is free since sign-up, as old as can be.
    public Task HandleAsync(UserProfileSnapshotIntegrationEvent e, CancellationToken ct) =>
        e.Plan is { } plan ? ApplyAsync(e, e.UserId, plan, e.PlanChangedAtUtc ?? DateTimeOffset.MinValue, ct) : Task.CompletedTask;

    private Task ApplyAsync(IntegrationEvent e, string userId, string plan, DateTimeOffset changedAt, CancellationToken ct) =>
        db.HandleOnceAsync(e, InboxExtensions.HandlerName(InboxName, e), async () =>
        {
            var copy = await db.Plans.FindAsync([userId], ct);
            if (Kept(copy is null ? null : (copy.Plan, copy.ChangedAtUtc), plan, changedAt) is not { } kept)
            {
                return;
            }
            if (copy is null)
            {
                db.Plans.Add(new UserPlanCopy { UserId = userId, Plan = kept.Plan, ChangedAtUtc = kept.ChangedAtUtc });
            }
            else
            {
                (copy.Plan, copy.ChangedAtUtc) = kept;
            }
        }, ct);

    /// <summary>
    /// What the copy should hold after hearing of a plan, or null when it stays as it is.
    /// <list type="bullet">
    /// <item><b>No copy yet:</b> the plan is kept, unless it is free and never changed, which is what no row already means.</item>
    /// <item><b>A copy:</b> the plan is kept when it is as new as the copy or newer; an older one (a late event, a
    /// snapshot taken before a change) is left.</item>
    /// </list>
    /// </summary>
    public static (string Plan, DateTimeOffset ChangedAtUtc)? Kept((string Plan, DateTimeOffset ChangedAtUtc)? copy, string plan, DateTimeOffset changedAt) =>
        copy is null
            ? plan != Plans.Free || changedAt != DateTimeOffset.MinValue ? (plan, changedAt) : null
            : changedAt >= copy.Value.ChangedAtUtc ? (plan, changedAt) : null;
}
