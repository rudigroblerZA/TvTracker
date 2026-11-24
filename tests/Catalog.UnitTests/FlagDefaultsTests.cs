using System.Text.Json;
using TvTracker;

namespace Catalog.UnitTests;

/// <summary>
/// The fallback used when flagd can't answer (<see cref="FeatureFlags.DefaultFor"/>) is what flagd.json says, so an
/// outage changes nothing.
/// </summary>
public class FlagDefaultsTests
{
    [Fact]
    public void The_flags_on_in_flagd_json_are_the_ones_on_by_default_in_code()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "flagd.json")));
        var on = file.RootElement.GetProperty("flags").EnumerateObject()
            .Where(f => f.Value.GetProperty("variants").GetProperty(f.Value.GetProperty("defaultVariant").GetString()!).GetBoolean())
            .Select(f => f.Name);

        Assert.Equal(FeatureFlags.OnByDefault.Order(), on.Order());
    }

    [Fact]
    public void Every_flag_flagd_json_limits_to_plans_is_a_paid_insight_with_the_paid_plans_and_their_rule()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "flagd.json")));
        var limited = file.RootElement.GetProperty("flags").EnumerateObject()
            .Where(f => f.Value.GetProperty("metadata").TryGetProperty(FeatureFlags.PlansMetadata, out _))
            .ToList();
        using var rule = JsonDocument.Parse(FeatureFlags.PlanRule(["plus", "family"]));

        // Every limited flag, whatever its plans: one limited some other way would be missed by a filter on the paid plans.
        Assert.Equal(FeatureFlags.PaidInsights.Order(), limited.Select(f => f.Name).Order());
        Assert.All(limited, flag =>
        {
            Assert.Equal(["plus", "family"], flag.Value.GetProperty("metadata").GetProperty(FeatureFlags.PlansMetadata).GetString()!.Split(','));
            Assert.True(JsonElement.DeepEquals(rule.RootElement, flag.Value.GetProperty("targeting")), $"{flag.Name} has another targeting rule");
        });
        Assert.True(FeatureFlags.PaidPlans.SetEquals(["plus", "family"]));
        // And no flag has a targeting rule without saying which plans it is for.
        Assert.All(file.RootElement.GetProperty("flags").EnumerateObject().Where(f => f.Value.TryGetProperty("targeting", out _)),
            flag => Assert.Contains(flag.Name, FeatureFlags.PaidInsights));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("plus", true)]
    [InlineData("family", true)]
    [InlineData("", true)]
    [InlineData("free", false)]
    public void Without_the_flag_server_a_paid_insight_is_on_unless_the_caller_is_known_to_be_on_another_plan(string? plan, bool on)
    {
        var context = FeatureFlags.ContextFor(null, plan);

        Assert.Equal(on, FeatureFlags.DefaultFor(FeatureFlags.YearInTv, context));
        // One that isn't paid is on for everyone.
        Assert.True(FeatureFlags.DefaultFor(FeatureFlags.ShowDna, context));
    }

    /// <summary>
    /// The flags page can change a flag's plans, which only flagd knows; the fallback goes by what flagd last said.
    /// Each case uses a flag of its own, since what was seen is kept for the process.
    /// </summary>
    [Fact]
    public void Without_the_flag_server_a_flag_keeps_the_plans_it_was_last_seen_with()
    {
        var free = FeatureFlags.ContextFor(null, "free");
        var plus = FeatureFlags.ContextFor(null, "plus");

        // A paid insight opened to every plan stays open to Free.
        FeatureFlags.RememberPlans(FeatureFlags.Mixtape, null);
        Assert.True(FeatureFlags.DefaultFor(FeatureFlags.Mixtape, free));

        // A free feature put into the paid plans is kept from Free.
        FeatureFlags.RememberPlans(FeatureFlags.TvAtlas, "plus,family");
        Assert.False(FeatureFlags.DefaultFor(FeatureFlags.TvAtlas, free));
        Assert.True(FeatureFlags.DefaultFor(FeatureFlags.TvAtlas, plus));

        // A paid insight narrowed to Family is kept from Pro too.
        FeatureFlags.RememberPlans(FeatureFlags.TvClock, "family");
        Assert.False(FeatureFlags.DefaultFor(FeatureFlags.TvClock, plus));
        Assert.True(FeatureFlags.DefaultFor(FeatureFlags.TvClock, FeatureFlags.ContextFor(null, "family")));
    }

    /// <summary>
    /// Notifications decides per recipient with <see cref="FeatureFlags.ContextForUser"/>, which has no plan, so a plan
    /// rule on one of its flags would be ignored there and a free account would be sent a paid feature's alerts.
    /// </summary>
    [Theory]
    [InlineData(FeatureFlags.SeasonVerdict)]
    [InlineData(FeatureFlags.CrossoverAlerts)]
    [InlineData(FeatureFlags.WeeklyDigest)]
    public void A_flag_notifications_evaluates_without_a_plan_has_no_plan_rule(string flag)
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "flagd.json")));
        var definition = file.RootElement.GetProperty("flags").GetProperty(flag);

        Assert.False(definition.GetProperty("metadata").TryGetProperty(FeatureFlags.PlansMetadata, out _));
        Assert.False(definition.TryGetProperty("targeting", out _));
    }
}
