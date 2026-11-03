using Microsoft.Extensions.Caching.Memory;
using OpenFeature;
using OpenFeature.Model;
using OpenFeature.Providers.Memory;
using TvTracker;

namespace Catalog.UnitTests;

/// <summary>
/// Flag answers kept a few seconds per flag and context (CONTEXT.md, Feature flags): a cached answer is reused, and a
/// flag that targets plans is never answered with another context's result.
/// </summary>
public class CachedFlagsTests
{
    private static async Task<(CachedFlags Flags, InMemoryProvider Provider)> BuildAsync(Dictionary<string, Flag> flags)
    {
        var provider = new InMemoryProvider(flags);
        var domain = Guid.NewGuid().ToString();
        await Api.Instance.SetProviderAsync(domain, provider);
        return (new CachedFlags(Api.Instance.GetClient(domain), new MemoryCache(new MemoryCacheOptions())), provider);
    }

    private static Flag<bool> OffFor(string plan) => new(
        new Dictionary<string, bool> { ["on"] = true, ["off"] = false }, "on",
        context => context.GetValue(FeatureFlags.PlanAttribute)?.AsString == plan ? "off" : "on");

    [Fact]
    public async Task A_cached_answer_is_reused_until_it_expires()
    {
        var (flags, provider) = await BuildAsync(new() { ["ask"] = OffFor("never") });
        var context = FeatureFlags.ContextFor(null);
        Assert.True(await flags.IsEnabledAsync("ask", context, TestContext.Current.CancellationToken));

        // Turned off for everyone; the cached answer stands for CachedFlags.CacheFor.
        await provider.UpdateFlagsAsync(new Dictionary<string, Flag>
        {
            ["ask"] = new Flag<bool>(new Dictionary<string, bool> { ["on"] = true, ["off"] = false }, "off")
        });

        Assert.True(await flags.IsEnabledAsync("ask", context, TestContext.Current.CancellationToken));
        Assert.False(await flags.IsEnabledAsync("ask", FeatureFlags.ContextFor(null, "plus"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Each_plan_gets_its_own_answer()
    {
        var (flags, _) = await BuildAsync(new() { ["ask"] = OffFor(FeatureFlags.FreePlan) });
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await flags.IsEnabledAsync("ask", FeatureFlags.ContextFor(null, FeatureFlags.FreePlan), ct));
        Assert.True(await flags.IsEnabledAsync("ask", FeatureFlags.ContextFor(null, "plus"), ct));
        Assert.True(await flags.IsEnabledAsync("ask", FeatureFlags.ContextFor(null), ct));
    }

    [Fact]
    public void Contexts_differing_only_in_a_list_get_different_keys()
    {
        static EvaluationContext With(params string[] groups) => EvaluationContext.Builder()
            .SetTargetingKey("u").Set("groups", new Value(groups.Select(g => new Value(g)).ToList())).Build();

        Assert.NotEqual(CachedFlags.KeyOf("ask", With("a")), CachedFlags.KeyOf("ask", With("b")));
        Assert.Equal(CachedFlags.KeyOf("ask", With("a", "b")), CachedFlags.KeyOf("ask", With("a", "b")));
    }

    [Fact]
    public async Task An_unknown_flag_fails_open()
    {
        var (flags, _) = await BuildAsync([]);
        Assert.True(await flags.IsEnabledAsync("missing", FeatureFlags.ContextFor(null), TestContext.Current.CancellationToken));
    }
}
