using Analytics.Data;
using TvTracker.IntegrationEvents;
using static Analytics.UnitTests.Build;

namespace Analytics.UnitTests;

/// <summary>
/// What the per-show stages take a season to be: specials (season 0) are never one, a finale is the last to air, and
/// figures that tie come out the same way every run.
/// </summary>
public class ShowSeasonRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = new(2015, 1, 5, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Well_rated_specials_are_not_a_season_in_the_arc_the_best_season_or_the_trend()
    {
        var show = Show();
        List<EpisodeCopy> episodes =
        [
            .. Season(show, 0, LongAgo.AddMonths(-6), Repeat(9.8, 4)),
            .. Season(show, 1, LongAgo, Repeat(7.0, 8)),
            .. Season(show, 2, LongAgo.AddYears(1), Repeat(7.5, 8)),
            .. Season(show, 3, LongAgo.AddYears(2), Repeat(8.0, 8))
        ];
        var context = Context(Now, show, episodes);

        Assert.Equal([1, 2, 3], context.RatedSeasons.Select(s => s.Number));
        var arc = new PatternsInsight().Apply(context, Document(show, Now)).Patterns!.Arc!;
        Assert.Equal((3, true), (arc.PeakSeason, arc.PeakIsLatest));
        var seasons = new SeasonsInsight().Apply(context, Document(show, Now));
        Assert.Equal((3, 1), (seasons.BestSeason!.Number, seasons.WorstSeason!.Number));
        Assert.Equal("rising", seasons.Trend);
        // The specials' own finale isn't one of the show's.
        Assert.Equal(3, new PatternsInsight().Apply(context, Document(show, Now)).Patterns!.Finales!.Seasons);
    }

    [Theory]
    // 8.2 − 7.9 is 0.29999999999999893 in floating point: still a decline of the full margin.
    [InlineData(8.2, 7.9, 2)]
    [InlineData(8.2, 8.0, null)]
    public void A_decline_is_every_later_season_a_full_margin_below_the_peak(double peak, double later, int? declineFrom)
    {
        var show = Show();
        List<EpisodeCopy> episodes =
        [
            .. Season(show, 1, LongAgo, Repeat(peak, 8)),
            .. Season(show, 2, LongAgo.AddYears(1), Repeat(later, 8)),
            .. Season(show, 3, LongAgo.AddYears(2), Repeat(later, 8))
        ];

        var arc = new PatternsInsight().Apply(Context(Now, show, episodes), Document(show, Now)).Patterns!.Arc!;

        Assert.Equal((1, declineFrom), (arc.PeakSeason, arc.DeclineFromSeason));
    }

    [Fact]
    public void A_seasons_finale_is_the_last_to_air_not_the_highest_numbered()
    {
        var show = Show();
        // Episode 10 aired mid-season; episode 9 was held back and aired last.
        var episodes = Season(show, 1, LongAgo, Repeat(8.0, 8));
        episodes.Add(Episode(show, 1, 10, LongAgo.AddDays(7 * 8), 8.0));
        episodes.Add(Episode(show, 1, 9, LongAgo.AddDays(7 * 9), 9.0));

        var finales = new PatternsInsight().Apply(Context(Now, show, episodes), Document(show, Now)).Patterns!.Finales!;

        Assert.Equal((1, 1, 1.0), (finales.Seasons, finales.BeatSeasonAverage, finales.AverageDifference));
    }

    [Fact]
    public void Top_episodes_that_tie_come_out_in_episode_order_whatever_order_they_were_loaded_in()
    {
        var show = Show();
        var episodes = Season(show, 1, LongAgo, Repeat(8.5, 6)).Concat(Season(show, 2, LongAgo.AddYears(1), Repeat(8.5, 6))).ToList();
        episodes.Reverse();

        var top = new TopEpisodesInsight().Apply(Context(Now, show, episodes), Document(show, Now)).TopEpisodes;

        Assert.Equal([(1, 1), (1, 2), (1, 3), (1, 4), (1, 5)], top.Select(e => (e.SeasonNumber, e.EpisodeNumber)));
    }

    [Fact]
    public void A_special_still_to_air_is_not_the_next_premiere_and_specials_are_not_catch_up()
    {
        var show = Show();
        List<EpisodeCopy> episodes =
        [
            .. Season(show, 0, LongAgo.AddMonths(-6), Repeat(8.0, 2)),
            Episode(show, 0, 3, Now.AddDays(10), null),
            .. Season(show, 1, LongAgo, Repeat(8.0, 8)),
            Episode(show, 2, 1, Now.AddDays(30), null)
        ];

        var premiere = new NextPremiereInsight().Apply(Context(Now, show, episodes), Document(show, Now)).NextPremiere!;

        Assert.Equal((2, 8, 8 * 45), (premiere.SeasonNumber, premiere.CatchUpEpisodes, premiere.CatchUpRuntimeMinutes));
    }
}
