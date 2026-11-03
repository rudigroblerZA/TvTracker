using Microsoft.Extensions.Caching.Memory;
using OpenFeature;
using Tracking.API.Data;
using TvTracker;

namespace Tracking.API.Services;

/// <summary>
/// The caller's plan for feature flags, from Tracking's copy (<see cref="UserPlanCopy"/>); no row means free.
/// </summary>
public sealed class CopiedUserPlans(CachedFlags flags, IMemoryCache cache) : UserPlansBase(flags, cache)
{
    protected override async Task<string?> LookUpAsync(HttpContext context, string userId) =>
        (await context.RequestServices.GetRequiredService<TrackingContext>().Plans.FindAsync([userId], context.RequestAborted))?.Plan
        ?? TvTracker.IntegrationEvents.Plans.Free;
}
