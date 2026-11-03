using System.Text;
using Microsoft.Extensions.Caching.Memory;
using OpenFeature;
using OpenFeature.Model;

namespace TvTracker;

/// <summary>
/// Boolean flag answers kept a few seconds per flag and evaluation context, in front of OpenFeature's
/// <see cref="IFeatureClient"/>.
/// <list type="bullet">
/// <item><b>Why:</b> the flagd provider's own cache is off (in OpenFeature.Providers.Flagd 0.8.0, still the latest,
/// every cached evaluation failed with a NullReferenceException and returned the default), so each evaluation was a gRPC
/// call to flagd. A gated request made two: the <see cref="FeatureFlags.Plans"/> switch for the caller's plan, then the
/// feature itself.</item>
/// <item><b>Keyed by the whole context</b> (targeting key, email, plan), so a flag that targets users or plans is never
/// answered with someone else's result.</item>
/// <item><b>How stale:</b> a flag turned off on the flags page or in flagd.json applies within <see cref="CacheFor"/>,
/// in every process.</item>
/// <item><b>What's left uncached:</b> the <c>/ops</c> flag list, which shows flagd as it is.</item>
/// </list>
/// Scoped, like <see cref="IFeatureClient"/>; the answers live in the process-wide <see cref="IMemoryCache"/>.
/// </summary>
public sealed class CachedFlags(IFeatureClient client, IMemoryCache cache)
{
    /// <summary>
    /// How long an answer is kept.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether <paramref name="flag"/> is on for <paramref name="context"/>, defaulting to on (fail open) when flagd
    /// can't answer.
    /// </summary>
    public async Task<bool> IsEnabledAsync(string flag, EvaluationContext context, CancellationToken cancellationToken = default)
    {
        var key = (nameof(CachedFlags), KeyOf(flag, context));
        if (cache.TryGetValue(key, out bool known))
        {
            return known;
        }
        var enabled = await client.GetBooleanValueAsync(flag, FeatureFlags.DefaultValue, context, cancellationToken: cancellationToken);
        cache.Set(key, enabled, CacheFor);
        return enabled;
    }

    /// <summary>
    /// The flag and every attribute of the context, sorted, as one string.
    /// </summary>
    internal static string KeyOf(string flag, EvaluationContext context)
    {
        var key = new StringBuilder(flag).Append('\u001f').Append(context.TargetingKey);
        foreach (var (name, value) in context.AsDictionary().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            key.Append('\u001f').Append(name).Append('=').Append(value.AsString ?? value.AsObject?.ToString());
        }
        return key.ToString();
    }
}
