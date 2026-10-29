using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>
/// Our users' star ratings of the show (from the minimum number of ratings) and the provider's episode ratings.
/// </summary>
public sealed class HistogramsInsight : IShowInsight
{
    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var stars = new int[5];
        foreach (var (star, count) in show.Sources.ShowStarCounts[show.ShowId])
        {
            stars[star - 1] = count;
        }

        // Half-point buckets from the lowest to the highest rating, empty ones included so the shape reads right.
        var counts = show.RatedAired
            .GroupBy(e => Math.Floor(e.ExternalRating!.Value * 2) / 2)
            .ToDictionary(g => g.Key, g => g.Count());
        var buckets = new List<HistogramBucket>();
        if (counts.Count > 0)
        {
            for (var from = counts.Keys.Min(); from <= counts.Keys.Max(); from += 0.5)
            {
                buckets.Add(new HistogramBucket(from, counts.GetValueOrDefault(from)));
            }
        }

        return insights with { Histograms = new Histograms(stars.Sum() >= InsightRules.MinRatingsToShow ? stars : null, buckets) };
    }
}
