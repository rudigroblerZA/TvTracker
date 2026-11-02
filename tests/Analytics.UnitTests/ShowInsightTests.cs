using Analytics.Data;
using Analytics.Worker.Insights;
using TvTracker.IntegrationEvents;
using static Analytics.UnitTests.Build;

namespace Analytics.UnitTests;

/// <summary>
/// The worker's show stages, on a show built in memory (see CONTEXT.md, When it gets good, Does it stick the landing?, Season verdict).
/// </summary>
public class ShowInsightTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = new(2015, 1, 5, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_slow_start_turns_where_the_ratings_settle()
    {
        var show = Show();
        var episodes = Season(show, 1, LongAgo, [7.0, 7.0, 7.0, .. Repeat(8.5, 17)]);

        var gets = new GetsGoodInsight().Apply(Context(Now, show, episodes), Document(show, Now)).GetsGood;

        Assert.NotNull(gets);
        Assert.False(gets.FromTheStart);
        Assert.Equal((1, 4), (gets.Turn!.SeasonNumber, gets.Turn.EpisodeNumber));
        Assert.Equal((3, 135), (gets.Turn.EpisodesBefore, gets.Turn.MinutesBefore));
        Assert.Equal((7.0, 8.5), (gets.Turn.Before, gets.Turn.After));
    }

    [Fact]
    public void Good_from_the_start()
    {
        var show = Show();
        var gets = new GetsGoodInsight().Apply(Context(Now, show, Season(show, 1, LongAgo, Repeat(8.2, 12))), Document(show, Now)).GetsGood;

        Assert.True(gets?.FromTheStart);
        Assert.Null(gets!.Turn);
    }

    [Theory]
    // Opening 0.2 below the bar: neither a strong nor a slow start, so nothing is said.
    [InlineData(8.3, 12)]
    // Too few rated episodes to tell.
    [InlineData(7.0, 6)]
    public void Nothing_said(double opening, int count)
    {
        var show = Show();
        var episodes = Season(show, 1, LongAgo, [opening, opening, opening, .. Repeat(8.5, count - 3)]);

        Assert.Null(new GetsGoodInsight().Apply(Context(Now, show, episodes), Document(show, Now)).GetsGood);
    }

    [Fact]
    public void A_strong_finale_sticks_the_landing()
    {
        var (show, episodes) = Ended(Repeat(8.0, 10), Repeat(8.0, 10), [.. Repeat(8.0, 9), 9.0]);

        var landing = new LandingInsight().Apply(Context(Now, show, episodes), Document(show, Now)).Landing;

        Assert.Equal(LandingVerdicts.Sticks, landing?.Verdict);
        Assert.Equal((9.0, 8.0), (landing!.FinaleRating, landing.ShowMedian));
        Assert.Equal((0, 29), (landing.RatedHigher, landing.RatedLower));
    }

    [Fact]
    public void A_weak_season_and_finale_fizzle_out()
    {
        var (show, episodes) = Ended(Repeat(8.0, 10), Repeat(8.0, 10), [.. Repeat(7.0, 9), 6.0]);

        Assert.Equal(LandingVerdicts.Fizzles, new LandingInsight().Apply(Context(Now, show, episodes), Document(show, Now)).Landing?.Verdict);
    }

    [Fact]
    public void A_great_finale_after_a_weaker_season()
    {
        var (show, episodes) = Ended(Repeat(8.0, 10), Repeat(8.0, 10), [.. Repeat(7.0, 9), 9.5]);

        Assert.Equal(LandingVerdicts.GreatFinale, new LandingInsight().Apply(Context(Now, show, episodes), Document(show, Now)).Landing?.Verdict);
    }

    [Fact]
    public void A_running_show_has_no_landing()
    {
        var (show, episodes) = Ended(Repeat(8.0, 10), [.. Repeat(8.0, 9), 9.0]);
        show.Status = ShowStatus.Airing;

        Assert.Null(new LandingInsight().Apply(Context(Now, show, episodes), Document(show, Now)).Landing);
    }

    [Fact]
    public void A_season_better_than_the_last_is_called_above_once_another_is_announced()
    {
        var show = Show();
        var finaleAt = Now.AddDays(-10);
        var episodes = Season(show, 1, finaleAt.AddYears(-1).AddDays(-63), Repeat(7.5, 10))
            .Concat(Season(show, 2, finaleAt.AddDays(-63), Repeat(8.3, 10))).ToList();

        // Ten days after a weekly finale isn't quiet enough on its own (twice the usual gap)...
        Assert.Null(new SeasonVerdictInsight().Apply(Context(Now, show, episodes), Document(show, Now)).SeasonVerdict);

        // ...but an announced season says it's over, and the verdict comes three days after the finale.
        var announced = new AnnouncedSeason { ShowId = show.ShowId, SeasonNumber = 3 };
        var verdict = new SeasonVerdictInsight().Apply(Context(Now, show, episodes, [announced]), Document(show, Now)).SeasonVerdict;

        Assert.NotNull(verdict);
        Assert.Equal((2, SeasonVerdicts.Above, FinaleVerdicts.InLine), (verdict.Number, verdict.Verdict, verdict.Finale));
        Assert.Equal(7.5, verdict.OthersMedian);
        Assert.Equal(finaleAt.UtcDateTime + SeasonVerdict.From, verdict.ReadyAtUtc);
    }

    [Fact]
    public void The_best_of_three_seasons_is_best_yet_and_a_strong_finale_is_strong()
    {
        var show = Show(ShowStatus.Ended);
        var finaleAt = Now.AddDays(-5);
        var episodes = Season(show, 1, finaleAt.AddYears(-2), Repeat(7.6, 8))
            .Concat(Season(show, 2, finaleAt.AddYears(-1), Repeat(7.8, 8)))
            .Concat(Season(show, 3, finaleAt.AddDays(-49), [.. Repeat(8.2, 7), 9.0])).ToList();

        var verdict = new SeasonVerdictInsight().Apply(Context(Now, show, episodes), Document(show, Now)).SeasonVerdict;

        Assert.Equal((SeasonVerdicts.BestYet, FinaleVerdicts.Strong), (verdict?.Verdict, verdict?.Finale));
    }

    [Fact]
    public void No_verdict_once_it_is_old_news()
    {
        var show = Show(ShowStatus.Ended);
        var episodes = Season(show, 1, Now.AddDays(-110), Repeat(8.0, 8));

        Assert.Null(new SeasonVerdictInsight().Apply(Context(Now, show, episodes), Document(show, Now)).SeasonVerdict);
    }

    [Theory]
    [InlineData(new[] { 3.0, 1.0, 2.0 }, 2.0)]
    [InlineData(new[] { 4.0, 1.0, 2.0, 3.0 }, 2.5)]
    public void Median(double[] values, double median) => Assert.Equal(median, InsightMath.Median(values));

    [Fact]
    public void Margins_survive_floating_point() => Assert.True(InsightMath.Reaches(8.1 - 7.8, 0.3));

    /// <summary>
    /// An ended show with one weekly season per rating list, years ago.
    /// </summary>
    private static (ShowCopy Show, List<EpisodeCopy> Episodes) Ended(params double?[][] seasons)
    {
        var show = Show(ShowStatus.Ended);
        var episodes = seasons.SelectMany((ratings, i) => Season(show, i + 1, LongAgo.AddYears(i), ratings)).ToList();
        return (show, episodes);
    }
}
