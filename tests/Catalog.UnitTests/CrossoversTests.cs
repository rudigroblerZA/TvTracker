using Catalog.API.Services;

namespace Catalog.UnitTests;

/// <summary>
/// Crossover events: same-named episodes of different shows within a week, part markers ignored.
/// </summary>
public class CrossoversTests
{
    private static readonly Guid Fire = Guid.NewGuid(), Med = Guid.NewGuid(), Pd = Guid.NewGuid();
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parts_of_one_event_share_its_name()
    {
        var labels = Crossovers.Label([
            (Fire, "Infection, Part I", Monday),
            (Med, "Infection, Part II", Monday.AddHours(1)),
            (Pd, "Infection (Part III)", Monday.AddHours(2))
        ]);

        Assert.Equal(new string?[] { "Infection", "Infection", "Infection" }, labels);
    }

    [Fact]
    public void Same_show_twice_is_not_a_crossover() =>
        Assert.All(Crossovers.Label([(Fire, "Infection, Part I", Monday), (Fire, "Infection, Part II", Monday.AddDays(7))]), Assert.Null);

    [Fact]
    public void Too_far_apart_is_not_a_crossover() =>
        Assert.All(Crossovers.Label([(Fire, "Homecoming", Monday), (Med, "Homecoming", Monday.AddDays(8))]), Assert.Null);

    [Theory]
    [InlineData("TBA")]
    [InlineData("Episode 5")]
    [InlineData("Ep")]
    public void Placeholder_titles_never_match(string title) =>
        Assert.All(Crossovers.Label([(Fire, title, Monday), (Med, title, Monday.AddHours(1))]), Assert.Null);

    [Fact]
    public void Unrelated_episodes_stay_unlabelled()
    {
        var labels = Crossovers.Label([
            (Fire, "Hold the Door", Monday),
            (Med, "Blood Lines", Monday.AddHours(1)),
            (Pd, "Blood Lines", Monday.AddHours(2))
        ]);

        Assert.Equal(new string?[] { null, "Blood Lines", "Blood Lines" }, labels);
    }
}
