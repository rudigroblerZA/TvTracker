namespace TvTracker.IntegrationEvents;

// Shared by Analytics (which computes DNA), Recommendations (which compares it) and the UI (which shows it; TvTracker.UI
// links this file), so all three use the same traits, weights and labels.

/// <param name="Weight">How much the trait counts when comparing two shows' DNA.</param>
/// <param name="Low">What a value near 0 means, e.g. "Short episodes".</param>
/// <param name="High">What a value near 1 means, e.g. "Hour-long episodes".</param>
/// <param name="Typical">The catalog's median: what an unremarkable show looks like (hour-long, weekly, recent).</param>
public record DnaTraitDefinition(string Key, string Name, double Weight, string Low, string High, double Typical);

/// <summary>
/// A show's DNA: traits of its form, rhythm and quality, each 0-1 between two plain-language ends, computed by
/// Analytics from episodes, ratings and our users' viewing. Shared here because Recommendations compares DNAs and
/// explains matches with the same labels.
/// </summary>
public static class DnaTraits
{
    public const string EpisodeLength = "episode-length";
    public const string SeasonSize = "season-size";
    public const string RunLength = "run-length";
    public const string Release = "release";
    public const string Pace = "pace";
    public const string Era = "era";
    public const string Acclaim = "acclaim";
    public const string Consistency = "consistency";
    public const string Trajectory = "trajectory";
    public const string Peak = "peak";
    public const string Finales = "finales";
    public const string Completion = "completion";

    public static readonly IReadOnlyList<DnaTraitDefinition> All =
    [
        // Typical values: medians over the 617-show development catalog (completion: too few viewers yet, so 0.5).
        new(EpisodeLength, "Episode length", 1, "Short episodes", "Hour-long episodes", 1),
        new(SeasonSize, "Season size", 1, "Short seasons", "Long seasons", 0.39),
        new(RunLength, "Run length", 1, "A short watch", "A long commitment", 0.48),
        new(Release, "Release", 0.7, "Weekly episodes", "Drops all at once", 0),
        new(Pace, "Wait between seasons", 0.5, "Back every year", "Long waits between seasons", 0.51),
        new(Era, "Era", 0.8, "A classic", "Recent", 0.8),
        new(Acclaim, "Acclaim", 1, "Mixed reviews", "Acclaimed", 0.44),
        new(Consistency, "Consistency", 0.8, "Rollercoaster", "Steady", 0.68),
        new(Trajectory, "Trajectory", 0.6, "Fades over time", "Gets better", 0.47),
        new(Peak, "Peak", 0.4, "Peaks early", "Peaks late", 0.5),
        new(Finales, "Finales", 0.4, "Quiet finales", "Big finales", 0.48),
        new(Completion, "Completion", 0.5, "Often abandoned", "Usually finished", 0.5)
    ];

    public static readonly IReadOnlyDictionary<string, DnaTraitDefinition> ByKey = All.ToDictionary(t => t.Key);

    /// <summary>Two shows are compared only when they share at least this many traits.</summary>
    public const int MinSharedTraits = 5;

    /// <summary>
    /// How alike two DNAs are, 0-1: the weighted average of 1 − |a − b| over the traits both have; null when they
    /// share fewer than <see cref="MinSharedTraits"/>.
    /// </summary>
    public static double? Similarity(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        var shared = a.Keys.Where(b.ContainsKey).Where(ByKey.ContainsKey).ToList();
        if (shared.Count < MinSharedTraits)
        {
            return null;
        }

        return shared.Sum(k => ByKey[k].Weight * (1 - Math.Abs(a[k] - b[k]))) / shared.Sum(k => ByKey[k].Weight);
    }

    /// <summary>A trait is distinctive when it's at least this far from the typical show's.</summary>
    public const double MinDistinctiveness = 0.25;

    /// <summary>
    /// How much a value sets a show apart (0 when it doesn't): its distance from the typical show's, when it also leans
    /// to one end. "Weekly episodes" says nothing about a show; "Drops all at once" does.
    /// </summary>
    public static double Distinctiveness(string key, double value) =>
        ByKey.TryGetValue(key, out var trait) && LabelFor(key, value) is not null && Math.Abs(value - trait.Typical) >= MinDistinctiveness
            ? Math.Abs(value - trait.Typical)
            : 0;

    /// <summary>The trait's plain-language end for a value, or null in the middle (0.3-0.7), where neither describes it.</summary>
    public static string? LabelFor(string key, double value) =>
        !ByKey.TryGetValue(key, out var trait) ? null : value <= 0.3 ? trait.Low : value >= 0.7 ? trait.High : null;
}
