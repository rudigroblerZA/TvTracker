using Analytics.Data;
using TvTracker.IntegrationEvents;
using static Analytics.Worker.Insights.InsightMath;

namespace Analytics.Worker.Insights;

/// <summary>
/// Whether an ended show sticks the landing (<see cref="Landing"/>): its finale (last aired regular episode) against the
/// median of its episodes, and its final season against the median of its seasons. Checked against the endings people
/// talk about: Game of Thrones, Dexter and How I Met Your Mother fizzle out (finales 1.7-2.7 below, weakest seasons);
/// The Americans, Battlestar Galactica and Succession stick it; The Office has a great finale after a weaker season.
/// </summary>
public sealed class LandingInsight : IShowInsight
{
    private const int MinRatedEpisodes = 10;
    private const double MinRatedShare = 0.6;

    /// <summary>A finale this far above or below the show's median episode is strong or weak; nearer, it's in line.</summary>
    private const double FinaleMargin = 0.3;

    /// <summary>A final season this far below the median season is a weak one; at or above it, it holds up.</summary>
    private const double SeasonMargin = 0.3;

    /// <summary>Seasons rank only with this many rated episodes, and the final season only among three or more.</summary>
    private const int MinSeasonEpisodes = 3;
    private const int MinRankedSeasons = 3;

    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights) =>
        insights with { Landing = LandingFor(show) };

    private static Landing? LandingFor(ShowInsightContext show)
    {
        if (show.Show.Status != ShowStatus.Ended)
        {
            return null;
        }

        var aired = show.Aired.Where(e => e.SeasonNumber > 0).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
        var rated = aired.Where(e => e.ExternalRating is not null).ToList();
        if (rated.Count < MinRatedEpisodes || rated.Count < aired.Count * MinRatedShare || aired[^1].ExternalRating is not { } finale)
        {
            return null;
        }

        var ratings = rated.Select(e => e.ExternalRating!.Value).ToList();
        var median = Median(ratings);
        var beats = (double)ratings.Count(r => r < finale) / (ratings.Count - 1);

        var seasons = rated.GroupBy(e => e.SeasonNumber)
            .Where(g => g.Count() >= MinSeasonEpisodes)
            .ToDictionary(g => g.Key, g => g.Average(e => e.ExternalRating!.Value));
        var finalSeason = aired[^1].SeasonNumber;
        int? rank = null;
        var seasonWeak = false;
        if (seasons.Count >= MinRankedSeasons && seasons.TryGetValue(finalSeason, out var finalAverage))
        {
            rank = seasons.Values.Count(v => v > finalAverage) + 1;
            seasonWeak = finalAverage <= Median(seasons.Values.ToList()) - SeasonMargin;
        }

        var verdict = (finale - median) switch
        {
            >= FinaleMargin => seasonWeak ? LandingVerdicts.GreatFinale : LandingVerdicts.Sticks,
            <= -FinaleMargin => seasonWeak ? LandingVerdicts.Fizzles : LandingVerdicts.FinaleLetsDown,
            _ => LandingVerdicts.AsItRan
        };

        return new Landing(verdict, aired.Select(e => e.SeasonNumber).Distinct().Count(), finale, Round(median), Round(beats), rank, seasons.Count);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
