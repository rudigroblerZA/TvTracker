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
    /// Whether <paramref name="flag"/> is on for <paramref name="context"/>, or its flagd.json default
    /// (<see cref="FeatureFlags.DefaultFor"/>) when flagd can't answer.
    /// </summary>
    public async Task<bool> IsEnabledAsync(string flag, EvaluationContext context, CancellationToken cancellationToken = default)
    {
        var key = (nameof(CachedFlags), KeyOf(flag, context));
        if (cache.TryGetValue(key, out bool known))
        {
            return known;
        }
        var enabled = await client.GetBooleanValueAsync(flag, FeatureFlags.DefaultFor(flag), context, cancellationToken: cancellationToken);
        cache.Set(key, enabled, CacheFor);
        return enabled;
    }

    /// <summary>
    /// The flag and every attribute of the context, sorted, as one string.
    /// </summary>
    public static string KeyOf(string flag, EvaluationContext context)
    {
        // Every part is length-prefixed, so no targeting key, name or value can make two contexts read alike.
        var key = new StringBuilder().Append(flag.Length).Append(':').Append(flag);
        key.Append(context.TargetingKey?.Length ?? -1).Append(':').Append(context.TargetingKey);
        foreach (var (name, value) in context.AsDictionary().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Append(key.Append(name.Length).Append(':').Append(name).Append('='), value);
            key.Append(';');
        }
        return key.ToString();
    }

    /// <summary>
    /// A value written out in full, lists and structures included, with its kind, so two contexts that differ in any
    /// attribute never share a key (a type name would make every list look the same).
    /// </summary>
    private static void Append(StringBuilder key, Value value)
    {
        if (value.IsNull) key.Append('n');
        else if (value.IsBoolean) key.Append('b').Append(value.AsBoolean);
        else if (value.IsNumber) key.Append('d').Append(value.AsDouble?.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        else if (value.IsString) key.Append('s').Append(value.AsString!.Length).Append(':').Append(value.AsString);
        else if (value.IsDateTime) key.Append('t').Append(value.AsDateTime?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        else if (value.IsList)
        {
            key.Append('[');
            foreach (var item in value.AsList!)
            {
                Append(key, item);
                key.Append(',');
            }
            key.Append(']');
        }
        else if (value.IsStructure)
        {
            key.Append('{');
            foreach (var (name, item) in value.AsStructure!.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                key.Append(name.Length).Append(':').Append(name).Append('=');
                Append(key, item);
                key.Append(',');
            }
            key.Append('}');
        }
        else key.Append('o').Append(value.AsObject);
    }
}
