using Analytics.Data;
using static Analytics.Worker.Insights.InsightMath;

namespace Analytics.Worker.Insights;

/// <summary>
/// When the show gets good (<see cref="GetsGood"/>), from the provider's ratings of its regular episodes in air order.
/// The bar is the median of the opening run (the first <see cref="OpeningRun"/> rated episodes), not the whole show:
/// long-running shows' ratings drift up over the years, which would otherwise put Friends' turn in season 2. A slow
/// start opens at least <see cref="SlowStartMargin"/> below the bar; it turns at the first episode where the next three
/// average the bar and the next ten stay within <see cref="StayMargin"/> of it. Checked against what viewers say: The
/// Office turns early in season 2, Succession at season 2's premiere, The Good Place and Community at episode 7.
/// </summary>
public sealed class GetsGoodInsight : IShowInsight
{
    private const int MinRatedEpisodes = 10;
    private const int OpeningRun = 40;
    private const int OpeningEpisodes = 3;

    /// <summary>
    /// Opening within this of the bar: good from the start.
    /// </summary>
    private const double StrongStartMargin = 0.1;

    /// <summary>
    /// Opening this far below the bar, or further: a slow start. In between, nothing is said.
    /// </summary>
    private const double SlowStartMargin = 0.3;

    private const double StayMargin = 0.05;

    /// <summary>
    /// The turn is looked for in the first this many rated episodes, and the first 60% of the opening run.
    /// </summary>
    private const int MaxTurn = 25;

    /// <summary>
    /// A turn is worth naming when the ten episodes from it beat the ones before by this much.
    /// </summary>
    private const double MinLift = 0.3;

    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights) =>
        insights with { GetsGood = GetsGoodFor(show) };

    private static GetsGood? GetsGoodFor(ShowInsightContext show)
    {
        var aired = show.Aired.Where(e => e.SeasonNumber > 0).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
        var rated = aired.Where(e => e.ExternalRating is not null).Take(OpeningRun).ToList();
        if (!show.WellRated || rated.Count < MinRatedEpisodes)
        {
            return null;
        }

        var ratings = rated.Select(e => e.ExternalRating!.Value).ToList();
        var settled = Median(ratings);
        var opening = ratings.Take(OpeningEpisodes).Average();
        if (opening >= settled - StrongStartMargin)
        {
            return new GetsGood(true, Round(opening), Round(settled), null);
        }
        if (opening > settled - SlowStartMargin)
        {
            return null;
        }

        var limit = Math.Min(MaxTurn, (int)(ratings.Count * 0.6));
        for (var i = OpeningEpisodes - 1; i < limit && i + 3 <= ratings.Count; i++)
        {
            var stays = ratings.Skip(i).Take(10).ToList();
            if (ratings.Skip(i).Take(3).Average() < settled || stays.Average() < settled - StayMargin)
            {
                continue;
            }

            var before = ratings.Take(i).Average();
            if (stays.Average() - before < MinLift)
            {
                return null;
            }

            var turn = rated[i];
            var earlier = aired.TakeWhile(e => e != turn).ToList();
            return new GetsGood(false, Round(opening), Round(settled), new GetsGoodTurn(
                turn.EpisodeId, turn.SeasonNumber, turn.EpisodeNumber, turn.Title, earlier.Count, earlier.Sum(e => e.RuntimeMinutes ?? 0),
                Round(before), Round(stays.Average())));
        }

        return null;
    }
}
