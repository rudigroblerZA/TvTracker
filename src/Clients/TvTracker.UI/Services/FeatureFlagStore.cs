using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components.Authorization;
using OpenFeature;
using TvTracker.IntegrationEvents;

namespace TvTracker.UI.Services;

/// <summary>
/// Evaluates feature flags (see <see cref="FeatureFlags"/>) for the signed-in user through OpenFeature. The host
/// registers the <see cref="IFeatureClient"/>: the web app's talks to flagd, the mobile app's goes through the BFF
/// over OFREP. Answers are kept for <see cref="CacheFor"/>, since the nav menu asks on every navigation and the
/// mobile app's evaluations are HTTP calls; a changed flag shows up within that time.
/// <para>
/// <b>Plans:</b> the user's plan (Identity's <c>/me/plan</c>, kept for <see cref="PlanCacheFor"/>) goes into the context, as
/// the services do, so a flag that's part of some plans only (<see cref="FeatureFlags.PlanRule"/>) is off here exactly when
/// the service would refuse it. <see cref="UnlockedByAsync"/> says which plan would turn an off feature on, so the UI can
/// offer it instead of hiding it. Like the services, nothing is limited while the <see cref="FeatureFlags.Plans"/> flag is
/// off or the plan can't be found.
/// </para>
/// </summary>
public sealed class FeatureFlagStore(IFeatureClient client, AuthenticationStateProvider authState, IdentityClient identity)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlanCacheFor = TimeSpan.FromMinutes(1);

    /// <summary>The plans an upgrade can be to, cheapest first.</summary>
    private static readonly string[] Upgrades = [Plans.Plus, Plans.Family];

    private readonly ConcurrentDictionary<(string Flag, string? User, string? Plan), (bool Enabled, DateTimeOffset At)> cache = new();
    private (string? User, string? Plan, DateTimeOffset At)? plan;
    private IReadOnlyList<PlanFeature>? planFeatures;

    public async Task<bool> IsEnabledAsync(string flag) => await EvaluateAsync(flag, await PlanAsync());

    /// <summary>
    /// The plan that would turn <paramref name="flag"/> on when it's off for the user only because of their plan
    /// (Plus first, then Family); null when it's on, or off for every plan.
    /// </summary>
    public async Task<string?> UnlockedByAsync(string flag)
    {
        var current = await PlanAsync();
        if (current is null || await EvaluateAsync(flag, current))
        {
            return null;
        }
        foreach (var upgrade in Upgrades)
        {
            if (upgrade != current && await EvaluateAsync(flag, upgrade))
            {
                return upgrade;
            }
        }
        return null;
    }

    /// <summary>
    /// The features that are part of some plans only, from the flags' own metadata (<see cref="FeatureFlags.PlansMetadata"/>
    /// and <see cref="FeatureFlags.NameMetadata"/>), so the plan page lists what flagd.json and the flags page say.
    /// </summary>
    public async Task<IReadOnlyList<PlanFeature>> PlanFeaturesAsync()
    {
        if (planFeatures is not null)
        {
            return planFeatures;
        }
        var context = FeatureFlags.ContextFor((await authState.GetAuthenticationStateAsync()).User);
        var details = await Task.WhenAll(FlagKeys.Select(async key => (key, details: await client.GetBooleanDetailsAsync(key, FeatureFlags.DefaultValue, context))));
        planFeatures = details
            .Where(d => d.details.FlagMetadata?.GetString(FeatureFlags.PlansMetadata) is { Length: > 0 })
            .Select(d => new PlanFeature(d.key, d.details.FlagMetadata!.GetString(FeatureFlags.NameMetadata) ?? d.key,
                d.details.FlagMetadata.GetString(FeatureFlags.PlansMetadata)!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
            .OrderBy(f => f.Name, StringComparer.CurrentCulture)
            .ToList();
        return planFeatures;
    }

    /// <summary>Forgets every answer, so a flag changed on the flags page (or a new plan) shows here at once.</summary>
    public void Clear()
    {
        cache.Clear();
        plan = null;
        planFeatures = null;
    }

    private async Task<bool> EvaluateAsync(string flag, string? forPlan)
    {
        var user = (await authState.GetAuthenticationStateAsync()).User;
        var context = FeatureFlags.ContextFor(user, forPlan);
        var key = (flag, context.TargetingKey, forPlan);
        if (cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheFor)
        {
            return cached.Enabled;
        }

        // OpenFeature never throws for a failed evaluation; it returns the default (on), so an outage keeps features.
        var enabled = await client.GetBooleanValueAsync(flag, FeatureFlags.DefaultValue, context);
        cache[key] = (enabled, DateTimeOffset.UtcNow);
        return enabled;
    }

    /// <summary>The user's plan for the flag context: free when signed out, null while plans are off or unknown.</summary>
    private async Task<string?> PlanAsync()
    {
        var user = (await authState.GetAuthenticationStateAsync()).User;
        var id = FeatureFlags.ContextFor(user).TargetingKey;
        if (plan is { } known && known.User == id && DateTimeOffset.UtcNow - known.At < PlanCacheFor)
        {
            return known.Plan;
        }
        string? found = null;
        if (await EvaluateAsync(FeatureFlags.Plans, null))
        {
            if (user.Identity?.IsAuthenticated != true)
            {
                found = FeatureFlags.FreePlan;
            }
            else
            {
                try
                {
                    found = (await identity.GetMyPlanAsync())?.Plan;
                }
                catch (Exception ex) when (ex is HttpRequestException or SignInRequiredException)
                {
                    // Unknown: plan rules let them in, as the services do.
                }
            }
        }
        plan = (id, found, DateTimeOffset.UtcNow);
        return found;
    }

    /// <summary>Every flag key in <see cref="FeatureFlags"/> (its string constants, less the ones that aren't flags).</summary>
    private static readonly string[] FlagKeys = typeof(FeatureFlags).GetFields()
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .Except([FeatureFlags.PlanAttribute, FeatureFlags.FreePlan, FeatureFlags.PlansMetadata, FeatureFlags.NameMetadata])
        .ToArray();
}

/// <summary>A feature that's part of some plans only.</summary>
public record PlanFeature(string Key, string Name, IReadOnlyList<string> Plans);

/// <summary>Cascaded by <c>Feature ShowLocked</c> to content shown while its feature is locked to <paramref name="Plan"/>.</summary>
public record FeatureLock(string Plan);
