using Microsoft.Extensions.Caching.Memory;
using OpenFeature;
using Recommendations.API.Data;
using TvTracker;

namespace Recommendations.API.Services;

/// <summary>The caller's plan for feature flags, from Recommendations's copy (<see cref="UserPlanCopy"/>); no row means free.</summary>
public sealed class CopiedUserPlans(IFeatureClient flags, IMemoryCache cache) : UserPlansBase(flags, cache)
{
    protected override async Task<string?> LookUpAsync(HttpContext context, string userId) =>
        (await context.RequestServices.GetRequiredService<RecommendationsContext>().Plans.FindAsync([userId], context.RequestAborted))?.Plan
        ?? TvTracker.IntegrationEvents.Plans.Free;
}
