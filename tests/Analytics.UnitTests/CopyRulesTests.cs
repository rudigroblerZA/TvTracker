using Analytics.Data;
using TvTracker.IntegrationEvents;

namespace Analytics.UnitTests;

/// <summary>
/// The readings every insight shares.
/// </summary>
public class CopyRulesTests
{
    private static readonly Guid ShowA = Guid.NewGuid(), ShowB = Guid.NewGuid();
    private static readonly DateTime Moment = new(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Eight_of_a_show_at_one_moment_is_a_bulk_mark()
    {
        var watches = Watches(ShowA, Moment, CopyRules.BulkMark)
            .Concat(Watches(ShowA, Moment.AddMinutes(1), CopyRules.BulkMark - 1))
            .Concat(Watches(ShowB, Moment, CopyRules.BulkMark - 1));

        Assert.Equal([(ShowA, Moment)], CopyRules.BulkMarks(watches));
    }

    [Theory]
    [InlineData(ShowStatus.Ended, true)]
    [InlineData(ShowStatus.Cancelled, true)]
    [InlineData(ShowStatus.Airing, false)]
    public void Ended_or_cancelled_is_over(ShowStatus status, bool ended) =>
        Assert.Equal(ended, new ShowCopy { Title = "x", Status = status }.HasEnded());

    [Fact]
    public void Aired_regular_leaves_out_specials_and_the_unaired()
    {
        var now = new DateTimeOffset(Moment);
        var show = Build.Show();
        var episodes = new[]
        {
            Build.Episode(show, 1, 1, now.AddDays(-7), 8),
            Build.Episode(show, 0, 1, now.AddDays(-7), 8),
            Build.Episode(show, 1, 2, now.AddDays(7), null),
            Build.Episode(show, 1, 3, null, null)
        };

        Assert.Equal([episodes[0]], episodes.AsQueryable().AiredRegular(now));
    }

    [Theory]
    [InlineData(6.0, 0, WorthItVerdicts.HardToRecommend)]
    [InlineData(5.0, 0, WorthItVerdicts.HardToRecommend)]
    [InlineData(7.5, 50, WorthItVerdicts.Mostly)]
    [InlineData(7.0, 33, WorthItVerdicts.Mixed)]
    [InlineData(8.4, 80, WorthItVerdicts.WorthIt)]
    [InlineData(9.6, 100, WorthItVerdicts.WorthIt)]
    public void Worth_it_scale(double value, int score, string verdict)
    {
        Assert.Equal(score, WorthItScale.Score(value));
        Assert.Equal(verdict, WorthItScale.Verdict(score));
    }

    private static IEnumerable<UserEpisodeWatch> Watches(Guid show, DateTime at, int count) =>
        Enumerable.Range(0, count).Select(_ => new UserEpisodeWatch { UserId = "u", ShowId = show, EpisodeId = Guid.NewGuid(), WatchedAtUtc = at });
}
