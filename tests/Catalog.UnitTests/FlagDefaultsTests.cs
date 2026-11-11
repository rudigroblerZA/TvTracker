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
}
