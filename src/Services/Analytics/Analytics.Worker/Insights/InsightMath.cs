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

    /// <summary>
    /// Whether <paramref name="difference"/> reaches <paramref name="margin"/>, give or take floating-point noise:
    /// 8.1 − 7.8 is 0.29999999999999982. Rounding the difference instead would also move a real −0.295 (a season
    /// median of 8.125) to −0.30.
    /// </summary>
    public static bool Reaches(double difference, double margin) => difference >= margin - 1e-9;

    /// <summary>The middle value, or the mean of the two middle ones; <paramref name="values"/> needn't be sorted.</summary>
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>"45 min", "6.5 h" or "33 h".</summary>
    public static string Hours(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes < 600 ? $"{minutes / 60.0:0.#} h" : $"{Math.Round(minutes / 60.0)} h";
}
