using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenFeature;

namespace TvTracker;

/// <summary>
/// The caller's plan, for the <see cref="FeatureFlags.PlanAttribute"/> of their flag context, so a feature flag can be part
/// of some plans only (<see cref="FeatureFlags.PlanRule"/>). <c>RequireFeature</c> uses it when one is registered.
/// </summary>
public interface IUserPlans
{
    /// <summary>
    /// "free", "plus" or "family"; <see cref="FeatureFlags.FreePlan"/> when signed out; null when plans don't apply (the
    /// <see cref="FeatureFlags.Plans"/> flag is off for the caller) or the plan can't be found, so plan rules let them in.
    /// </summary>
    Task<string?> PlanAsync(HttpContext context);
}

/// <summary>
/// The shared part of every plan source: the <see cref="FeatureFlags.Plans"/> switch, signed-out callers, and a short cache
/// per user; a service supplies the lookup (<see cref="LookUpAsync"/>). Registered scoped, since OpenFeature's
/// <see cref="IFeatureClient"/> is (as a singleton, every service that registered one failed to start); the cache is the
/// process-wide <see cref="IMemoryCache"/>.
/// <list type="bullet">
/// <item><b>From the service's own copy where it has one</b> (Tracking, Analytics, Social, Recommendations keep
/// <c>user_plans</c> from Identity's events), as their limits already do: no call per request, and no reference to
/// Identity, which references those services itself (for deletion and export) and so can't be referenced back: Aspire
/// deadlocked starting the cycle.</item>
/// <item><b>Else from Identity</b> (<see cref="IdentityUserPlans"/>), for services Identity doesn't call.</item>
/// <item><b>Fail open:</b> a lookup that fails gives null, so no plan rule applies; never a paid feature taken from someone
/// who pays because a service is down.</item>
/// </list>
/// </summary>
public abstract class UserPlansBase(IFeatureClient flags, IMemoryCache cache) : IUserPlans
{
    /// <summary>How long a user's plan is kept: a change reaches the service within this time.</summary>
    protected virtual TimeSpan CacheFor => TimeSpan.FromSeconds(30);

    public async Task<string?> PlanAsync(HttpContext context)
    {
        var user = context.User;
        if (!await flags.GetBooleanValueAsync(FeatureFlags.Plans, FeatureFlags.DefaultValue, FeatureFlags.ContextFor(user),
                cancellationToken: context.RequestAborted))
        {
            return null;
        }
        if (user.Identity?.IsAuthenticated != true)
        {
            return FeatureFlags.FreePlan;
        }
        if ((user.FindFirst("sub") ?? user.FindFirst(ClaimTypes.NameIdentifier))?.Value is not { } id)
        {
            return null;
        }

        var key = (nameof(UserPlansBase), id);
        if (cache.TryGetValue(key, out string? known))
        {
            return known;
        }
        var plan = await LookUpAsync(context, id);
        cache.Set(key, plan, plan is null ? TimeSpan.FromSeconds(10) : CacheFor);
        return plan;
    }

    /// <summary>The user's plan, or null when it can't be found.</summary>
    protected abstract Task<string?> LookUpAsync(HttpContext context, string userId);
}

/// <summary>
/// Asks Identity (<c>GET /api/v1/me/plan</c>, with the caller's own token), for services that keep no copy of plans.
/// Identity answers with the plan the user gets, so a family member reads "family". Kept a minute per user.
/// </summary>
public sealed class IdentityUserPlans(IHttpClientFactory http, IFeatureClient flags, IMemoryCache cache, ILogger<IdentityUserPlans> logger)
    : UserPlansBase(flags, cache)
{
    public const string ClientName = "identity-plans";

    protected override TimeSpan CacheFor => TimeSpan.FromMinutes(1);

    private sealed record PlanAnswer(string Plan);

    protected override async Task<string?> LookUpAsync(HttpContext context, string userId)
    {
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var bearer))
        {
            return null;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/me/plan");
            request.Headers.Authorization = bearer;
            using var response = await http.CreateClient(ClientName).SendAsync(request, context.RequestAborted);
            return response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<PlanAnswer>(context.RequestAborted) is { Plan.Length: > 0 } answer
                ? answer.Plan
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogDebug(ex, "Couldn't look up a plan; plan rules let the caller in for now");
            return null;
        }
    }
}
