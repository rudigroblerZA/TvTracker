using Analytics.API.Apis;
using static Analytics.API.Apis.StreaksApi;

namespace Analytics.UnitTests;

/// <summary>
/// Streaks: days and weeks in a row, and shows kept up with as they air.
/// </summary>
public class StreaksTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    [Fact]
    public void A_run_ending_yesterday_is_alive_but_at_risk()
    {
        var days = Dates(-3, -2, -1);
        var streak = Runs(days, d => d.DayNumber, Today.DayNumber);

        Assert.Equal(new StreakDto(3, 3, Today.AddDays(-3), Today.AddDays(-1), AtRisk: true), streak);
    }

    [Fact]
    public void Watching_today_keeps_it_safe() =>
        Assert.Equal(new StreakDto(4, 4, Today.AddDays(-3), Today, AtRisk: false), Runs(Dates(-3, -2, -1, 0), d => d.DayNumber, Today.DayNumber));

    [Fact]
    public void A_missed_day_ends_the_run_but_not_the_record()
    {
        var streak = Runs(Dates(-10, -9, -8, -7, -3), d => d.DayNumber, Today.DayNumber);

        Assert.Equal(new StreakDto(0, 4, Today.AddDays(-10), Today.AddDays(-7), AtRisk: false), streak);
    }

    [Fact]
    public void No_watches_no_streak() => Assert.Equal(new StreakDto(0, 0, null, null, false), Runs([], d => d.DayNumber, Today.DayNumber));

    [Theory]
    [InlineData(2026, 11, 2, 2026, 11, 2)]
    [InlineData(2026, 11, 1, 2026, 10, 26)]
    [InlineData(2026, 11, 4, 2026, 11, 2)]
    [InlineData(2027, 1, 1, 2026, 12, 28)]
    public void Weeks_start_on_monday(int y, int m, int d, int my, int mm, int md) =>
        Assert.Equal(new DateOnly(my, mm, md), WeekStart(new DateOnly(y, m, d)));

    [Fact]
    public void Weeks_in_a_row_across_the_year_end()
    {
        var mondays = new[] { new DateOnly(2026, 12, 21), new DateOnly(2026, 12, 28), new DateOnly(2027, 1, 4) };
        var streak = Runs([.. mondays], d => d.DayNumber / 7, new DateOnly(2027, 1, 6).DayNumber / 7);

        Assert.Equal(3, streak.Current);
        Assert.False(streak.AtRisk);
    }

    [Fact]
    public void Kept_up_after_a_late_start_with_the_next_episode_due()
    {
        // MobLand, as tested end to end: the first episode unwatched and the second late (both before the first kept one,
        // so no miss), four kept, the latest aired one still inside its week.
        var show = Guid.NewGuid();
        var e = Weekly(show, new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero), 7);
        var watched = Watched((e[1], 15), (e[2], 1), (e[3], 1), (e[4], 1), (e[5], 1));

        var run = KeptUp(e, watched, Now);

        Assert.Equal(4, run.Current);
        Assert.Equal(4, run.Longest);
        Assert.Equal(6, run.Latest!.Episode);
        var due = Assert.Single(run.Due);
        Assert.Equal(7, due.Episode.Episode);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero), due.Deadline);
    }

    [Fact]
    public void A_late_episode_breaks_the_run_but_the_record_stays()
    {
        // Scrubs: three kept, one watched a fortnight late, two kept.
        var show = Guid.NewGuid();
        var e = Weekly(show, new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero), 6);
        var watched = Watched((e[0], 1), (e[1], 2), (e[2], 1), (e[3], 15), (e[4], 1), (e[5], 1));

        var run = KeptUp(e, watched, Now);

        Assert.Equal(2, run.Current);
        Assert.Equal(3, run.Longest);
        Assert.Equal(3, run.LongestEnd!.Episode);
        Assert.Empty(run.Due);
    }

    [Theory]
    [InlineData(-1.0, true)]
    [InlineData(-1.5, false)]
    [InlineData(7.0, true)]
    [InlineData(7.1, false)]
    public void The_window_is_a_day_before_to_a_week_after(double daysAfterAiring, bool kept)
    {
        var e = Weekly(Guid.NewGuid(), Now.AddDays(-60), 1);
        var run = KeptUp(e, new Dictionary<Guid, DateTimeOffset> { [e[0].EpisodeId] = e[0].AirsAtUtc.AddDays(daysAfterAiring) }, Now);

        Assert.Equal(kept ? 1 : 0, run.Longest);
    }

    [Fact]
    public void Nothing_kept_nothing_due()
    {
        // Episodes before the first kept one aren't counted at all, due or not.
        var e = Weekly(Guid.NewGuid(), Now.AddDays(-20), 3);
        var run = KeptUp(e, new Dictionary<Guid, DateTimeOffset>(), Now);

        Assert.Equal(new KeptUpRun(0, 0, null, null, []), run with { Due = [] });
        Assert.Empty(run.Due);
    }

    [Fact]
    public void Year_in_tv_counts_consecutive_days() =>
        Assert.Equal(3, YearInTvApi.LongestStreak(Dates(-9, -8, -5, -4, -3, -3, -1)));

    private static List<DateOnly> Dates(params int[] offsets) => offsets.Select(o => Today.AddDays(o)).ToList();

    private static List<Aired> Weekly(Guid show, DateTimeOffset first, int count) =>
        Enumerable.Range(0, count).Select(i => new Aired(Guid.NewGuid(), show, 2, i + 1, first.AddDays(7 * i))).ToList();

    private static Dictionary<Guid, DateTimeOffset> Watched(params (Aired Episode, double DaysLater)[] marks) =>
        marks.ToDictionary(m => m.Episode.EpisodeId, m => m.Episode.AirsAtUtc.AddDays(m.DaysLater));
}
