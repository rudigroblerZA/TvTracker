using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>
/// Per-season stats, the best and worst season, and whether the show is rising or falling.
/// </summary>
public sealed class SeasonsInsight : IShowInsight
{
    /// <summary>
    /// Change in rating per season (0-10 scale) beyond which a show is "rising" or "falling"
    /// .</summary>
    private const double TrendThreshold = 0.05;

    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var (best, worst) = BestAndWorst(show.Seasons);
        return insights with { Seasons = show.SeasonStats, BestSeason = best, WorstSeason = worst, Trend = Trend(show.SeasonSlope) };
    }

    /// <summary>
    /// Best and worst season by the provider's ratings, or by ours when the provider has none.
    /// Needs two seasons with enough rated episodes; worst is omitted when it would equal best.
    /// </summary>
    private static (SeasonHighlight? Best, SeasonHighlight? Worst) BestAndWorst(IReadOnlyList<SeasonSummary> seasons)
    {
        var byExternal = seasons
            .Where(s => s.Stats.Rating.External is not null && s.RatingCounts)
            .Select(s => (s.Stats.Number, Rating: s.Stats.Rating.External!.Value))
            .ToList();
        var (source, ranked) = byExternal.Count >= 2
            ? ("external", byExternal)
            : ("ours", seasons.Where(s => s.Stats.Rating.Ours is not null).Select(s => (s.Stats.Number, Rating: s.Stats.Rating.Ours!.Value)).ToList());

        if (ranked.Count < 2)
        {
            return (null, null);
        }

        var best = ranked.MaxBy(s => s.Rating);
        var worst = ranked.MinBy(s => s.Rating);
        return (new SeasonHighlight(best.Number, best.Rating, source),
            worst.Rating < best.Rating ? new SeasonHighlight(worst.Number, worst.Rating, source) : null);
    }

    /// <summary>
    /// "rising", "falling" or "steady" from <see cref="ShowInsightContext.SeasonSlope"/>; null with fewer than three rated seasons.
    /// </summary>
    private static string? Trend(double? slope) => slope switch
    {
        null => null,
        > TrendThreshold => "rising",
        < -TrendThreshold => "falling",
        _ => "steady"
    };
}
