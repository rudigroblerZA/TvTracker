using Analytics.Data;
using Microsoft.Extensions.Caching.Memory;
using OpenFeature;
using TvTracker;

namespace Analytics.API.Services;

/// <summary>The caller's plan for feature flags, from Analytics' copy (<see cref="UserPlanCopy"/>); no row means free.</summary>
public sealed class CopiedUserPlans(IFeatureClient flags, IMemoryCache cache) : UserPlansBase(flags, cache)
{
    protected override async Task<string?> LookUpAsync(HttpContext context, string userId) =>
        (await context.RequestServices.GetRequiredService<AnalyticsContext>().Plans.FindAsync([userId], context.RequestAborted))?.Plan
        ?? TvTracker.IntegrationEvents.Plans.Free;
}
