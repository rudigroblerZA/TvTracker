using Microsoft.Extensions.Caching.Memory;
using OpenFeature;
using Social.API.Data;
using TvTracker;

namespace Social.API.Services;

/// <summary>The caller's plan for feature flags, from Social's copy (<see cref="UserPlanCopy"/>); no row means free.</summary>
public sealed class CopiedUserPlans(IFeatureClient flags, IMemoryCache cache) : UserPlansBase(flags, cache)
{
    protected override async Task<string?> LookUpAsync(HttpContext context, string userId) =>
        (await context.RequestServices.GetRequiredService<SocialContext>().Plans.FindAsync([userId], context.RequestAborted))?.Plan
        ?? TvTracker.IntegrationEvents.Plans.Free;
}
