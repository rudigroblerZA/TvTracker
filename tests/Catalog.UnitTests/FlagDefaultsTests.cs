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
    public void The_paid_insights_in_code_are_the_flags_flagd_json_gives_to_the_paid_plans()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "flagd.json")));
        var paid = file.RootElement.GetProperty("flags").EnumerateObject()
            .Where(f => f.Value.GetProperty("metadata").TryGetProperty(FeatureFlags.PlansMetadata, out var plans)
                && plans.GetString()!.Split(',').ToHashSet().SetEquals(FeatureFlags.PaidPlans))
            .Select(f => f.Name);

        Assert.Equal(FeatureFlags.PaidInsights.Order(), paid.Order());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("plus", true)]
    [InlineData("family", true)]
    [InlineData("free", false)]
    public void Without_the_flag_server_a_paid_insight_is_on_unless_the_caller_is_known_to_be_on_another_plan(string? plan, bool on)
    {
        var context = FeatureFlags.ContextFor(null, plan);

        Assert.Equal(on, FeatureFlags.DefaultFor(FeatureFlags.YearInTv, context));
        // One that isn't paid is on for everyone.
        Assert.True(FeatureFlags.DefaultFor(FeatureFlags.ShowDna, context));
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
