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

    /// <summary>The final season is ranked only among this many seasons that count.</summary>
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

        // Counted, not as a share: ratings have one decimal, so ties are common, and "lower than any other episode"
        // must mean none rates the same or lower.
        var others = ratings.Count - 1;
        var higher = ratings.Count(r => r > finale);
        var lower = ratings.Count(r => r < finale);

        // The seasons that count (3+ rated episodes; see SeasonSummary), specials left out. The final season is
        // checked whenever it counts, even with only two seasons; it's ranked only among three or more.
        var seasons = show.RatedSeasons.Where(s => s.Number > 0).ToList();
        var finalSeason = seasons.FirstOrDefault(s => s.Number == aired[^1].SeasonNumber);
        bool? seasonWeak = seasons.Count == 1 ? false
            : finalSeason == default ? null
            : Round(finalSeason.Rating - Median(seasons.Select(s => s.Rating).ToList())) <= -SeasonMargin;
        int? rank = seasons.Count >= MinRankedSeasons && finalSeason != default
            ? seasons.Count(s => s.Rating > finalSeason.Rating) + 1
            : null;

        // Rounded first: on one-decimal ratings, 8.1 - 7.8 is 0.29999999999999982.
        var verdict = Round(finale - median) switch
        {
            >= FinaleMargin => seasonWeak switch { true => LandingVerdicts.GreatFinale, false => LandingVerdicts.Sticks, null => null },
            <= -FinaleMargin => seasonWeak switch { true => LandingVerdicts.Fizzles, _ => LandingVerdicts.FinaleLetsDown },
            _ => seasonWeak == true ? LandingVerdicts.WeakerLastSeason : LandingVerdicts.AsItRan
        };

        // A strong finale whose final season can't be checked (too few rated episodes) isn't called a landing either way.
        return verdict is null
            ? null
            : new Landing(verdict, finale, Round(median),
                Round((double)lower / others), rank, rank is null ? 0 : seasons.Count, higher, lower, others);
    }
}
