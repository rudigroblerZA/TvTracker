using Analytics.Data;
using TvTracker.IntegrationEvents;
using static Analytics.UnitTests.Build;

namespace Analytics.UnitTests;

/// <summary>
/// Edges of the per-viewer rules: a season dropped at once, a slow pace, and how few taste twins is too few.
/// </summary>
public class ViewerEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_season_dropped_at_once_is_finished_by_its_last_numbered_episode_whatever_order_it_was_loaded_in()
    {
        var show = Show(ShowStatus.Ended);
        var dropped = new DateTimeOffset(2025, 6, 1, 7, 0, 0, TimeSpan.Zero);
        var episodes = Enumerable.Range(1, 4).Select(n => Episode(show, 1, n, dropped, 8.0)).ToList();
        // Watched over two years: the last numbered episode, the finale, this year.
        var watchedAt = new[] { new DateTime(2025, 12, 28), new DateTime(2025, 12, 29), new DateTime(2025, 12, 30), new DateTime(2026, 1, 3) };
        var all = episodes.Select((e, i) => (show.ShowId, e.EpisodeId, watchedAt[i])).ToList();
        var inYear = new[] { new YearInTv.Watch(show.ShowId, episodes[3].EpisodeId, watchedAt[3], DateOnly.FromDateTime(watchedAt[3]), 45, 1, 4, "Finale", null) };
        var shows = new Dictionary<Guid, ShowCopy> { [show.ShowId] = show };

        foreach (var loaded in new[] { episodes, Enumerable.Reverse(episodes).ToList() })
        {
            var aired = loaded.ToLookup(e => e.ShowId, e => (e.EpisodeId, e.AirsAtUtc, e.SeasonNumber, e.EpisodeNumber));

            var (finished, _, _) = YearInTv.Progress([show.ShowId], aired, all, shows, inYear, Now.UtcDateTime);

            Assert.Equal([show.ShowId], finished);
        }
    }

    [Fact]
    public void A_finish_date_comes_from_the_pace_itself_not_the_rounded_figure_shown()
    {
        var show = Show();
        var episodes = Season(show, 1, Now.AddYears(-2), Repeat(8.0, 11));
        // One episode in the last four weeks: a quarter of an episode a week, shown as 0.2.
        var watches = new[] { (show.ShowId, episodes[0].EpisodeId, Now.UtcDateTime.AddDays(-3)) }.ToLookup(w => w.ShowId, w => (w.EpisodeId, w.Item3));
        var progress = new ShowProgress(show.ShowId, 11, 1, 1, false, 450, episodes[^1].AirsAtUtc, Now.UtcDateTime.AddDays(-3));

        var outlook = Assert.Single(FinishOutlook.Of([progress], [progress], new Dictionary<Guid, ShowCopy> { [show.ShowId] = show },
            episodes.ToLookup(e => e.ShowId), watches, new Dictionary<Guid, ShowInsights>(), Now));

        // Ten left at a quarter a week is 40 weeks; at the rounded 0.2 it would be 50.
        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime.AddDays(280)), outlook.FinishBy);
    }

    [Fact]
    public void Fewer_twins_than_the_privacy_minimum_is_no_twins_at_all()
    {
        var four = Enumerable.Range(1, InsightRules.MinUsersToShow - 1).Select(i => new TasteTwins.Twin($"user-{i}", 5, 0.9)).ToList();

        Assert.False(TasteTwins.Enough(four));
        var answer = TasteTwins.Of(rated: 8, four, [], [], new Dictionary<Guid, ShowCopy>());
        Assert.Equal((0, null), (answer.Twins, answer.Agreement));
        Assert.NotNull(answer.Reason);
        Assert.True(TasteTwins.Enough([.. four, new TasteTwins.Twin("user-5", 5, 0.9)]));
    }

    [Fact]
    public void Twins_who_agree_equally_are_chosen_the_same_way_whatever_order_they_come_in()
    {
        var candidates = Enumerable.Range(1, TasteTwins.MaxTwins + 10).Select(i => ($"user-{i:000}", 4, 0.5)).ToList();

        var forwards = TasteTwins.Twins(candidates).Select(t => t.UserId).ToList();
        var backwards = TasteTwins.Twins(Enumerable.Reverse(candidates)).Select(t => t.UserId).ToList();

        Assert.Equal(forwards, backwards);
        Assert.Equal(TasteTwins.MaxTwins, forwards.Count);
    }
}
