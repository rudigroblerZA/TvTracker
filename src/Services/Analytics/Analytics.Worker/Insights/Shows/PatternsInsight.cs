using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>How the show's episode ratings behave: finales, each season's best episode, consistency and the show's arc.</summary>
public sealed class PatternsInsight : IShowInsight
{
    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var aired = show.Aired;
        var rated = show.RatedAired;
        var ratedBySeason = rated.ToLookup(e => e.SeasonNumber);

        // A season's last episode is its finale only once the whole season has aired. It's compared with the
        // season's other episodes.
        var finaleDifferences = show.Seasons
            .Where(s => s.RatingCounts && s.Stats.AiredCount == s.Stats.EpisodeCount)
            .Select(s =>
            {
                var finale = aired.Where(e => e.SeasonNumber == s.Stats.Number).MaxBy(e => e.EpisodeNumber);
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

        return insights with { Patterns = new EpisodePatterns(finales, seasonBests, consistency, ArcFor(show.Seasons)) };
    }

    /// <summary>
    /// The best-rated season, and the season a decline set in: the first after the peak from which every rated
    /// season stayed at least <see cref="InsightRules.DeclineMargin"/> below it.
    /// </summary>
    private static Arc? ArcFor(IReadOnlyList<SeasonSummary> seasons)
    {
        var points = seasons
            .Where(s => s.Stats.Rating.External is not null && s.RatingCounts)
            .Select(s => (s.Stats.Number, Rating: s.Stats.Rating.External!.Value))
            .ToList();
        if (points.Count < 3)
        {
            return null;
        }

        var peakIndex = points.FindIndex(p => p.Rating == points.Max(x => x.Rating));
        var peak = points[peakIndex];

        int? declineFrom = null;
        for (var i = peakIndex + 1; i < points.Count; i++)
        {
            if (points.Skip(i).All(p => p.Rating <= peak.Rating - InsightRules.DeclineMargin))
            {
                declineFrom = points[i].Number;
                break;
            }
        }

        return new Arc(peak.Number, peak.Rating, declineFrom, peakIndex == points.Count - 1);
    }
}
