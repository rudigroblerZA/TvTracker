using Microsoft.AspNetCore.Components.Authorization;
using OpenFeature;
using System.Collections.Concurrent;

namespace TvTracker.UI.Services;

/// <summary>
/// Evaluates feature flags (see <see cref="FeatureFlags"/>) for the signed-in user through OpenFeature. The host
/// registers the <see cref="IFeatureClient"/>: the web app's talks to flagd, the mobile app's goes through the BFF
/// over OFREP. Answers are kept for <see cref="CacheFor"/>, since the nav menu asks on every navigation and the
/// mobile app's evaluations are HTTP calls; a changed flag shows up within that time.
/// </summary>
public sealed class FeatureFlagStore(IFeatureClient client, AuthenticationStateProvider authState)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(string Flag, string? User), (bool Enabled, DateTimeOffset At)> cache = new();

    public async Task<bool> IsEnabledAsync(string flag)
    {
        var user = (await authState.GetAuthenticationStateAsync()).User;
        var context = FeatureFlags.ContextFor(user);
        var key = (flag, context.TargetingKey);
        if (cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheFor)
        {
            return cached.Enabled;
        }

        // OpenFeature never throws for a failed evaluation; it returns the default (on), so an outage keeps features.
        var enabled = await client.GetBooleanValueAsync(flag, FeatureFlags.DefaultValue, context);
        cache[key] = (enabled, DateTimeOffset.UtcNow);
        return enabled;
    }

    /// <summary>Forgets every answer, so a flag changed on the flags page shows here at once.</summary>
    public void Clear() => cache.Clear();
}
