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
