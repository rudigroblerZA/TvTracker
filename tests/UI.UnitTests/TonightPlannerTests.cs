using TvTracker.UI.Models;
using TvTracker.UI.Services;

namespace UI.UnitTests;

/// <summary>
/// Tonight's planner's rules: it runs over by at most max(5 minutes, a twelfth of the evening),
/// "one show" plans one, and a mix uses up to three shows, none taking more than three-quarters of the evening unless
/// that would leave it under three-quarters full.
/// </summary>
public class TonightPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 19, 0, 0, TimeSpan.Zero);

    private static TonightShow Show(string title, int episodes, int minutes, int daysSinceWatched = 3) =>
        new(Guid.NewGuid(), title, null, ["Drama"], WatchStatus.Watching, Now.UtcDateTime.AddDays(-daysSinceWatched), 4,
            Enumerable.Range(1, episodes).Select(i => new TonightEpisode(Guid.NewGuid(), 1, i, $"Episode {i}", minutes, false)).ToList());

    private static TonightPlan Plan(IReadOnlyList<TonightShow> queue, int budget, bool oneShow,
        IReadOnlyDictionary<Guid, double>? fits = null) =>
        TonightPlanner.Plan(queue, fits, fits is null ? null : "light", budget, oneShow, new HashSet<Guid>(), Now);

    [Theory]
    [InlineData(60)]
    [InlineData(150)]
    [InlineData(240)]
    public void A_plan_never_runs_over_by_more_than_its_tolerance(int budget)
    {
        var plan = Plan([Show("Hour-long", 8, 58), Show("Sitcom", 10, 22), Show("Limited", 6, 47)], budget, oneShow: false);

        Assert.InRange(plan.Minutes, 1, budget + Math.Max(5, budget / 12));
    }

    [Fact]
    public void One_show_means_one_show()
    {
        var plan = Plan([Show("Hour-long", 8, 58), Show("Sitcom", 10, 22)], 150, oneShow: true);

        Assert.Single(plan.Blocks);
    }

    [Fact]
    public void A_mix_takes_at_most_three_shows_and_none_takes_more_than_three_quarters()
    {
        var queue = new[] { Show("A", 6, 30), Show("B", 6, 30), Show("C", 6, 30), Show("D", 6, 30), Show("E", 6, 30) };
        var fits = queue.ToDictionary(s => s.ShowId, _ => 0.8);

        var plan = Plan(queue, 150, oneShow: false, fits);

        Assert.InRange(plan.Blocks.Count, 2, TonightPlanner.MaxShowsMixed);
        Assert.All(plan.Blocks, b => Assert.True(b.Minutes <= 150 * 0.75, $"{b.Show.Title} took {b.Minutes} of 150 minutes"));
    }

    [Fact]
    public void The_cap_gives_way_when_one_show_is_all_there_is_to_fill_the_evening()
    {
        // Only one show has enough episodes; capped at three-quarters the evening would be under three-quarters full.
        var plan = Plan([Show("Binge", 10, 30), Show("Short", 1, 20)], 150, oneShow: false);

        Assert.True(plan.Minutes >= 150 * 0.75, $"planned {plan.Minutes} of 150 minutes");
    }

    [Fact]
    public void Episodes_are_taken_in_order_from_the_next_one()
    {
        var show = Show("Ordered", 6, 40);

        var block = Assert.Single(Plan([show], 120, oneShow: true).Blocks);

        Assert.Equal(show.Episodes.Take(block.Episodes.Count).Select(e => e.EpisodeId), block.Episodes.Select(e => e.EpisodeId));
    }

    [Fact]
    public void A_better_mood_fit_wins_the_evening()
    {
        var light = Show("Light comedy", 6, 25);
        var heavy = Show("Heavy drama", 6, 25);
        var fits = new Dictionary<Guid, double> { [light.ShowId] = 1.0, [heavy.ShowId] = 0.0 };

        var plan = Plan([heavy, light], 50, oneShow: true, fits);

        Assert.Equal(light.ShowId, Assert.Single(plan.Blocks).Show.ShowId);
    }
}
