using Microsoft.Extensions.Caching.Memory;
using OpenFeature;
using OpenFeature.Model;
using OpenFeature.Providers.Memory;
using TvTracker;

namespace Catalog.UnitTests;

/// <summary>
/// Flag answers kept a few seconds per flag and context: a cached answer is reused, and a
/// flag that targets plans is never answered with another context's result.
/// </summary>
public class CachedFlagsTests
{
    /// <summary>
    /// The memory cache's clock, moved by hand.
    /// </summary>
    private sealed class ManualClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);
    }

    private static async Task<(CachedFlags Flags, InMemoryProvider Provider, ManualClock Clock)> BuildAsync(Dictionary<string, Flag> flags)
    {
        var provider = new InMemoryProvider(flags);
        var domain = Guid.NewGuid().ToString();
        await Api.Instance.SetProviderAsync(domain, provider);
        var clock = new ManualClock();
        return (new CachedFlags(Api.Instance.GetClient(domain), new MemoryCache(new MemoryCacheOptions { Clock = clock })), provider, clock);
    }

    private static Flag<bool> OffFor(string plan) => new(
        new Dictionary<string, bool> { ["on"] = true, ["off"] = false }, "on",
        context => context.GetValue(FeatureFlags.PlanAttribute)?.AsString == plan ? "off" : "on");

    [Fact]
    public async Task A_cached_answer_is_reused_until_it_expires()
    {
        var (flags, provider, clock) = await BuildAsync(new() { ["year-in-tv"] = OffFor("never") });
        var context = FeatureFlags.ContextFor(null);
        Assert.True(await flags.IsEnabledAsync("year-in-tv", context, TestContext.Current.CancellationToken));

        // Turned off for everyone; the cached answer stands for CachedFlags.CacheFor.
        await provider.UpdateFlagsAsync(new Dictionary<string, Flag>
        {
            ["year-in-tv"] = new Flag<bool>(new Dictionary<string, bool> { ["on"] = true, ["off"] = false }, "off")
        });

        Assert.True(await flags.IsEnabledAsync("year-in-tv", context, TestContext.Current.CancellationToken));
        Assert.False(await flags.IsEnabledAsync("year-in-tv", FeatureFlags.ContextFor(null, "plus"), TestContext.Current.CancellationToken));

        // Past CacheFor, the flag is asked again and the change shows.
        clock.UtcNow += CachedFlags.CacheFor + TimeSpan.FromSeconds(1);
        Assert.False(await flags.IsEnabledAsync("year-in-tv", context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Each_plan_gets_its_own_answer()
    {
        var (flags, _, _) = await BuildAsync(new() { ["year-in-tv"] = OffFor(FeatureFlags.FreePlan) });
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await flags.IsEnabledAsync("year-in-tv", FeatureFlags.ContextFor(null, FeatureFlags.FreePlan), ct));
        Assert.True(await flags.IsEnabledAsync("year-in-tv", FeatureFlags.ContextFor(null, "plus"), ct));
        Assert.True(await flags.IsEnabledAsync("year-in-tv", FeatureFlags.ContextFor(null), ct));
    }

    [Fact]
    public void Contexts_differing_only_in_a_list_get_different_keys()
    {
        static EvaluationContext With(params string[] groups) => EvaluationContext.Builder()
            .SetTargetingKey("u").Set("groups", new Value(groups.Select(g => new Value(g)).ToList())).Build();

        Assert.NotEqual(CachedFlags.KeyOf("year-in-tv", With("a")), CachedFlags.KeyOf("year-in-tv", With("b")));
        Assert.Equal(CachedFlags.KeyOf("year-in-tv", With("a", "b")), CachedFlags.KeyOf("year-in-tv", With("a", "b")));
    }

    [Fact]
    public async Task A_flag_flagd_cant_answer_for_is_its_default()
    {
        var (flags, _, _) = await BuildAsync([]);
        var context = FeatureFlags.ContextFor(null);
        Assert.True(await flags.IsEnabledAsync(FeatureFlags.Universes, context, TestContext.Current.CancellationToken));
        Assert.False(await flags.IsEnabledAsync(FeatureFlags.Payments, context, TestContext.Current.CancellationToken));
    }
}
