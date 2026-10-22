using Analytics.Data;

namespace Analytics.Worker.Insights;

internal static class InsightMath
{
    /// <summary>A rating pair whose average from our users shows only from <see cref="InsightRules.MinRatingsToShow"/> ratings.</summary>
    public static RatingPair Pair(double? external, (int Sum, int Count) ours) =>
        new(external, ours.Count >= InsightRules.MinRatingsToShow ? Average(ours) : null, ours.Count);

    public static double? Average((int Sum, int Count) ratings) =>
        ratings.Count == 0 ? null : Round((double)ratings.Sum / ratings.Count);

    public static double Round(double value) => Math.Round(value, 2);

    /// <summary>"45 min", "6.5 h" or "33 h".</summary>
    public static string Hours(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes < 600 ? $"{minutes / 60.0:0.#} h" : $"{Math.Round(minutes / 60.0)} h";
}
