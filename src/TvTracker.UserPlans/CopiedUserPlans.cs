using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace TvTracker.UserPlans;

/// <summary>
/// The caller's plan from the service's own copy (<see cref="UserPlanCopy"/>); no row means free.
/// </summary>
public sealed class CopiedUserPlans<TContext>(CachedFlags flags, IMemoryCache cache) : UserPlansBase(flags, cache)
    where TContext : DbContext, IUserPlanCopies
{
    // Not kept: the copy is a read by key in the request's own database, and a plan that has just changed (someone who
    // has just paid) must count at once, for limits and for plan rules on flags alike.
    protected override TimeSpan CacheFor => TimeSpan.Zero;

    protected override async Task<string?> LookUpAsync(HttpContext context, string userId) =>
        (await context.RequestServices.GetRequiredService<TContext>().Plans.FindAsync([userId], context.RequestAborted))?.Plan ?? Plans.Free;
}

public static class UserPlansExtensions
{
    /// <summary>
    /// Reads callers' plans from this service's copy in <typeparamref name="TContext"/>, in place of asking Identity.
    /// </summary>
    public static IServiceCollection AddCopiedUserPlans<TContext>(this IServiceCollection services)
        where TContext : DbContext, IUserPlanCopies =>
        services.AddUserPlans<CopiedUserPlans<TContext>>();

    /// <summary>
    /// The caller's plan and what it allows.
    /// </summary>
    public static async Task<PlanAllowance> AllowanceAsync(this IUserPlans plans, HttpContext context)
    {
        var (plan, applies) = await plans.OfAsync(context);
        return new PlanAllowance(plan ?? Plans.Free, applies && plan is not null ? Plans.Limits(plan) : null);
    }
}

/// <summary>
/// The caller's plan and its limits.
/// </summary>
/// <param name="Plan">Their plan; free when it couldn't be found.</param>
/// <param name="Limits">Null when nothing limits them: plans are switched off for them (the <c>plans</c> flag), or
/// their plan couldn't be found. Fail open, as plan rules on feature flags do: nobody who pays is refused because a
/// lookup failed.</param>
public sealed record PlanAllowance(string Plan, PlanLimits? Limits);
