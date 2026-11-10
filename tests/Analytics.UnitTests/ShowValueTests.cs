using Analytics.API.Apis;
using Analytics.Data;

namespace Analytics.UnitTests;

/// <summary>
/// Show value, shared by the backlog and time off.
/// </summary>
public class ShowValueTests
{
    [Fact]
    public void Rating_moved_by_taste_and_ending()
    {
        var value = ShowValue.Assess(8.0, ["Drama", "Crime"], LandingVerdicts.Fizzles, null, false, ["Drama"]);

        // Half the genres liked is neutral; fizzling out costs half a point.
        Assert.Equal(7.5, value.Value);
        Assert.Equal(["Rated 8.0", "Your kind of show: Drama", "Fizzles out at the end"], value.Reasons);
    }

    [Theory]
    [InlineData(LandingVerdicts.Sticks, 8.3)]
    [InlineData(LandingVerdicts.GreatFinale, 8.3)]
    [InlineData(LandingVerdicts.WeakerLastSeason, 7.9)]
    [InlineData(LandingVerdicts.FinaleLetsDown, 7.7)]
    [InlineData(null, 8.0)]
    public void Endings(string? landing, double value) => Assert.Equal(value, ShowValue.Assess(8.0, [], landing, null, false, []).Value);

    [Fact]
    public void All_genres_liked_adds_three_tenths() => Assert.Equal(8.3, ShowValue.Assess(8.0, ["Comedy"], null, null, false, ["Comedy"]).Value);

    [Fact]
    public void An_unrated_show_counts_as_seven() => Assert.Equal(ShowValue.UnratedValue, ShowValue.Assess(null, [], null, null, false, []).Value);

    [Fact]
    public void Only_a_slow_start_of_three_hours_or_more_is_named()
    {
        var quick = new GetsGoodTurn(Guid.NewGuid(), 1, 4, "x", 3, 120, 7.0, 8.5);
        var slow = quick with { SeasonNumber = 2, EpisodeNumber = 3, MinutesBefore = 600 };

        Assert.Null(ShowValue.Assess(8.0, [], null, quick, false, []).SlowStart);
        var value = ShowValue.Assess(8.0, [], null, slow, false, []);
        Assert.Equal(slow, value.SlowStart);
        Assert.Contains("Slow start: gets good at S02E03, 10 h in", value.Reasons);
    }
}
