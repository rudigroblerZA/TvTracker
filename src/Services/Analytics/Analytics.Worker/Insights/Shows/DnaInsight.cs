using Analytics.Data;
using TvTracker.IntegrationEvents;
using static Analytics.Worker.Insights.InsightMath;

namespace Analytics.Worker.Insights;

/// <summary>
/// The show's DNA (see <see cref="DnaTraits"/>): each trait is a measure scaled to 0-1 between fixed anchors. The
/// anchors are about the 10th and 90th percentiles over the 615-show development catalog, so most shows spread across
/// the range and a few sit at the ends. They're fixed rather than recomputed as percentiles, so a show's DNA stays put
/// as the catalog grows, and one show can be computed without the others. Runs after the patterns and viewing stages,
/// whose figures it reuses.
/// </summary>
public sealed class DnaInsight : IShowInsight
{
    /// <summary>Season length and run length are scaled on a log scale: 6 episodes to 12 is as big a step as 12 to 24.</summary>
    private static readonly (double Low, double High) EpisodeMinutes = (22, 60);
    private static readonly (double Low, double High) EpisodesPerSeason = (6, 22);
    private static readonly (double Low, double High) RunHours = (6, 200);
    private static readonly (double Low, double High) DaysBetweenSeasons = (120, 760);
    private static readonly (double Low, double High) FirstYear = (1995, 2025);
    private static readonly (double Low, double High) Rating = (6.8, 8.6);

    /// <summary>Episode-rating spread: 0.25 and under is steady (1), 0.75 and over a rollercoaster (0).</summary>
    private static readonly (double Low, double High) Spread = (0.25, 0.75);

    /// <summary>Rating points gained or lost per season.</summary>
    private static readonly (double Low, double High) Slope = (-0.25, 0.25);

    /// <summary>A finale's rating against the rest of its season, averaged.</summary>
    private static readonly (double Low, double High) FinaleDifference = (-0.1, 0.55);

    /// <summary>Fewer aired episodes than this, and the show has no DNA yet.</summary>
    private const int MinAiredEpisodes = 3;

    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var traits = new Dictionary<string, (double Value, string Detail)>();
        void Add(string key, double? value, Func<string> detail)
        {
            if (value is { } v && !double.IsNaN(v))
            {
                traits[key] = (Round(Math.Clamp(v, 0, 1)), detail());
            }
        }

        // Regular seasons only: specials (season 0) would skew season sizes and gaps.
        var aired = show.Aired.Where(e => e.SeasonNumber > 0).ToList();
        if (aired.Count < MinAiredEpisodes)
        {
            return insights with { Dna = null };
        }

        var runtimes = aired.Where(e => e.RuntimeMinutes > 0).Select(e => e.RuntimeMinutes!.Value).Order().ToList();
        if (runtimes.Count > 0)
        {
            var typical = runtimes[runtimes.Count / 2];
            Add(DnaTraits.EpisodeLength, Linear(typical, EpisodeMinutes), () => $"About {typical} min an episode");
        }

        var perSeason = (double)aired.Count / aired.Select(e => e.SeasonNumber).Distinct().Count();
        Add(DnaTraits.SeasonSize, Log(perSeason, EpisodesPerSeason), () => $"About {perSeason:0} episodes a season");
        var minutes = aired.Sum(e => e.RuntimeMinutes ?? 0);
        if (minutes > 0)
        {
            Add(DnaTraits.RunLength, Log(minutes / 60.0, RunHours), () => $"{Hours(minutes)} in all");
        }

        var (drops, fullSeasons) = Drops(show, aired);
        if (fullSeasons > 0)
        {
            Add(DnaTraits.Release, (double)drops / fullSeasons, () => drops == 0
                ? "Released weekly"
                : $"{drops} of {fullSeasons} season{(fullSeasons == 1 ? "" : "s")} released at once");
        }

        if (MedianGapDays(aired) is { } gap)
        {
            Add(DnaTraits.Pace, Log(gap, DaysBetweenSeasons), () => gap < 60
                ? $"About {gap:0} days between seasons"
                : $"About {gap / 30.4:0} months between seasons");
        }

        if (aired.Min(e => e.AirsAtUtc) is { } first)
        {
            Add(DnaTraits.Era, Linear(first.Year, FirstYear), () => $"First aired in {first.Year}");
        }

        if (show.Show.ExternalRating is { } rating)
        {
            Add(DnaTraits.Acclaim, Linear(rating, Rating), () => $"Rated {rating:0.0} on TVmaze");
        }

        if (insights.Patterns?.Consistency is { } consistency)
        {
            Add(DnaTraits.Consistency, 1 - Linear(consistency.Spread, Spread), () => $"Episode ratings vary by {consistency.Spread:0.00}");
        }

        if (show.SeasonSlope is { } slope)
        {
            Add(DnaTraits.Trajectory, Linear(slope, Slope), () => $"{(slope >= 0 ? "+" : "−")}{Math.Abs(slope):0.00} a season, on average");
        }

        // Where in its run the best season came: the first rated season 0, the last 1.
        var points = show.RatedSeasons;
        if (points.Count >= 3)
        {
            var peak = points.Select((p, i) => (p, i)).MaxBy(x => x.p.Rating);
            Add(DnaTraits.Peak, (double)peak.i / (points.Count - 1), () => $"Best season: {peak.p.Number} of {points[^1].Number}");
        }

        if (insights.Patterns?.Finales is { } finales)
        {
            Add(DnaTraits.Finales, Linear(finales.AverageDifference, FinaleDifference),
                () => $"Finales rate {Math.Abs(finales.AverageDifference):0.00} {(finales.AverageDifference >= 0 ? "above" : "below")} their season");
        }

        // Our users' viewing: only when Viewing passed the privacy minimum.
        if (insights.Viewing?.CompletionRate is { } completion)
        {
            Add(DnaTraits.Completion, completion, () => $"{completion * 100:0}% of our viewers finish it");
        }

        var dna = DnaTraits.All
            .Where(t => traits.ContainsKey(t.Key))
            .Select(t => new DnaTrait(t.Key, traits[t.Key].Value, DnaTraits.LabelFor(t.Key, traits[t.Key].Value), traits[t.Key].Detail))
            .ToList();
        return insights with { Dna = dna.Count == 0 ? null : new ShowDna(dna) };
    }

    /// <summary>
    /// Fully aired regular seasons of at least three episodes, and how many of them came out on one
    /// day or in a few batches (the same rule as the "drops all at once" tag).
    /// </summary>
    private static (int Drops, int Seasons) Drops(ShowInsightContext show, List<EpisodeCopy> aired)
    {
        var full = show.SeasonStats.Where(s => s.Number > 0 && s.AiredCount >= 3 && s.AiredCount == s.EpisodeCount).ToList();
        var drops = full.Count(s =>
        {
            var days = aired.Where(e => e.SeasonNumber == s.Number && e.AirsAtUtc is not null)
                .Select(e => e.AirsAtUtc!.Value.UtcDateTime.Date).Distinct().Count();
            return days == 1 || days <= ShowTags.MaxDropDays && s.AiredCount >= 2 * days;
        });
        return (drops, full.Count);
    }

    /// <summary>Median days from one season's last episode to the next one's first; null with fewer than two aired seasons.</summary>
    private static double? MedianGapDays(List<EpisodeCopy> aired)
    {
        var seasons = aired.Where(e => e.AirsAtUtc is not null)
            .GroupBy(e => e.SeasonNumber)
            .OrderBy(g => g.Key)
            .Select(g => (First: g.Min(e => e.AirsAtUtc!.Value), Last: g.Max(e => e.AirsAtUtc!.Value)))
            .ToList();
        var gaps = seasons.Zip(seasons.Skip(1), (a, b) => (b.First - a.Last).TotalDays).Where(d => d >= 0).ToList();
        return gaps.Count == 0 ? null : Median(gaps);
    }

    private static double Linear(double value, (double Low, double High) anchors) =>
        (value - anchors.Low) / (anchors.High - anchors.Low);

    private static double? Log(double value, (double Low, double High) anchors) =>
        value <= 0 ? null : (Math.Log(value) - Math.Log(anchors.Low)) / (Math.Log(anchors.High) - Math.Log(anchors.Low));
}
