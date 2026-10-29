using Identity.API.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Identity.API.Services;

/// <summary>
/// Works out each user's effective plan: the plan they pay for, or the family plan while they're a member of a family
/// whose owner pays for it. Every change that can move it (a plan set, a join, a leave, an owner's deletion) calls
/// <see cref="RecomputeAsync"/> for the people it touches, which sends <see cref="UserPlanChangedIntegrationEvent"/>
/// through the outbox for each whose plan moved, in the caller's save. Other services only ever see effective plans.
/// </summary>
public static class PlanState
{
    public static async Task RecomputeAsync(IdentityContext db, IIntegrationEventOutbox outbox, DateTimeOffset now,
        IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var profiles = await db.Profiles.Where(p => userIds.Contains(p.UserId)).ToListAsync(ct);

        // Memberships as they'll be once the caller saves: the stored ones, with its added and removed rows applied.
        var memberships = await db.FamilyMembers.AsNoTracking().Where(m => userIds.Contains(m.UserId))
            .ToDictionaryAsync(m => m.UserId, m => m.OwnerUserId, ct);
        foreach (var entry in db.ChangeTracker.Entries<FamilyMember>().Where(e => userIds.Contains(e.Entity.UserId)))
        {
            if (entry.State == EntityState.Deleted)
            {
                memberships.Remove(entry.Entity.UserId);
            }
            else
            {
                memberships[entry.Entity.UserId] = entry.Entity.OwnerUserId;
            }
        }

        // Owners' plans, likewise as they'll be: a plan just set counts, and a deleted owner pays for nothing.
        var ownerIds = memberships.Values.Distinct().ToList();
        var ownerPlans = await db.Profiles.AsNoTracking().Where(p => ownerIds.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, p => p.Plan, ct);
        foreach (var entry in db.ChangeTracker.Entries<UserProfile>().Where(e => ownerIds.Contains(e.Entity.UserId)))
        {
            ownerPlans[entry.Entity.UserId] = entry.State == EntityState.Deleted ? Plans.Free : entry.Entity.Plan;
        }

        foreach (var profile in profiles)
        {
            var effective = profile.Plan != Plans.Free ? profile.Plan
                : memberships.TryGetValue(profile.UserId, out var owner) && ownerPlans.GetValueOrDefault(owner) == Plans.Family ? Plans.Family
                : Plans.Free;
            if (effective != profile.EffectivePlan)
            {
                profile.EffectivePlan = effective;
                profile.EffectivePlanChangedAtUtc = now.UtcDateTime;
                outbox.Add(new UserPlanChangedIntegrationEvent(profile.UserId, effective, now));
            }
        }
    }

    /// <summary>The members of a family, for recomputing them when its owner's plan or account changes.</summary>
    public static Task<List<string>> MembersOfAsync(IdentityContext db, string ownerUserId, CancellationToken ct) =>
        db.FamilyMembers.Where(m => m.OwnerUserId == ownerUserId).Select(m => m.UserId).ToListAsync(ct);
}
