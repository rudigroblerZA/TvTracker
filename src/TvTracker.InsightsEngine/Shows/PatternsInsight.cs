
namespace TvTracker.InsightsEngine;

/// <summary>
/// How the show's episode ratings behave: finales, each season's best episode, consistency and the show's arc.
/// </summary>
public sealed class PatternsInsight : IShowInsight
{
    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var rated = show.RatedAired;
        var ratedBySeason = rated.ToLookup(e => e.SeasonNumber);

        // A season's last episode is its finale only once the whole season has aired. It's compared with the
        // season's other episodes. Regular seasons only: specials don't build to a finale.
        var finaleDifferences = show.Seasons
            .Where(s => s.Stats.Number > 0 && s.RatingCounts && s.Stats.AiredCount == s.Stats.EpisodeCount)
            .Select(s =>
            {
                var finale = show.FinaleOf(s.Stats.Number);
                var others = ratedBySeason[s.Stats.Number].Where(e => e != finale).ToList();
                return finale?.ExternalRating is { } rating && others.Count > 0
                    ? rating - others.Average(e => e.ExternalRating!.Value)
                    : (double?)null;
            })
            .OfType<double>()
            .ToList();
        var finales = finaleDifferences.Count == 0
            ? null
            : new FinaleStats(finaleDifferences.Count, finaleDifferences.Count(d => d > 0), InsightMath.Round(finaleDifferences.Average()));

        var seasonBests = ratedBySeason
            .OrderBy(g => g.Key)
            .Select(g => g.OrderByDescending(e => e.ExternalRating).ThenBy(e => e.EpisodeNumber).First())
            .Select(e => new SeasonBest(e.SeasonNumber, e.EpisodeId, e.EpisodeNumber, e.Title, e.ExternalRating!.Value))
            .ToList();

        Consistency? consistency = null;
        if (rated.Count >= InsightRules.MinRatedEpisodesForConsistency)
        {
            var mean = rated.Average(e => e.ExternalRating!.Value);
            var spread = InsightMath.Round(Math.Sqrt(rated.Sum(e => Math.Pow(e.ExternalRating!.Value - mean, 2)) / (rated.Count - 1)));
            consistency = new Consistency(spread,
                spread < InsightRules.SteadySpread ? "steady" : spread >= InsightRules.RollercoasterSpread ? "rollercoaster" : "varied");
        }

        return insights with { Patterns = new EpisodePatterns(finales, seasonBests, consistency, ArcFor(show.RatedSeasons)) };
    }

    /// <summary>
    /// The best-rated season, and the season a decline set in: the first after the peak from which every rated
    /// season stayed at least <see cref="InsightRules.DeclineMargin"/> below it.
    /// </summary>
    private static Arc? ArcFor(IReadOnlyList<(int Number, double Rating)> points)
    {
        if (points.Count < 3)
        {
            return null;
        }

        // The first season to reach the highest rating.
        var peak = points.MaxBy(p => p.Rating);
        var peakIndex = points.ToList().IndexOf(peak);

        int? declineFrom = null;
        for (var i = peakIndex + 1; i < points.Count; i++)
        {
            if (points.Skip(i).All(p => InsightMath.Reaches(peak.Rating - p.Rating, InsightRules.DeclineMargin)))
            {
                declineFrom = points[i].Number;
                break;
            }
        }

        return new Arc(peak.Number, peak.Rating, declineFrom, peakIndex == points.Count - 1);
    }
}
