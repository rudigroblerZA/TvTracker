using Catalog.Embeddings;

namespace Catalog.Mixes;

/// <summary>
/// The rules of a mix: several shows in, the shows that sit between all of them out. Blend is a mix of two with a
/// lean; Mixtape is an even mix of three to five. Pure: vectors and numbers only.
/// <list type="bullet">
/// <item><b>Weights</b> say how much each show counts, and add up to 1. An even mix gives each 1/n.</item>
/// <item><b>Score</b> (lower is better) is a candidate's distance to the show it is least like, each distance scaled
/// by n × that show's weight. In an even mix that is the plain distance to the farthest show, so a candidate close to
/// just one of them ranks low however close it is. Leaning towards a show makes distance from it cost more and
/// distance from the others less.</item>
/// <item><b>Distances</b> are cosine distances between L2-normalised vectors: 1 − the dot product.</item>
/// </list>
/// </summary>
public static class MixRules
{
    /// <summary>
    /// Each of <paramref name="count"/> shows counting the same.
    /// </summary>
    public static double[] EvenWeights(int count) => Enumerable.Repeat(1.0 / count, count).ToArray();

    /// <summary>
    /// Two shows, the first counting <paramref name="lean"/> percent (clamped to 0-100).
    /// </summary>
    public static double[] LeanWeights(int lean)
    {
        var first = Math.Clamp(lean, 0, 100) / 100.0;
        return [first, 1 - first];
    }

    /// <summary>
    /// The point the shows are mixed at: their weighted sum, normalised. Candidates are gathered around it.
    /// </summary>
    public static float[] Mix(IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights)
    {
        var mix = new float[vectors[0].Length];
        for (var v = 0; v < vectors.Count; v++)
        {
            for (var i = 0; i < mix.Length; i++)
            {
                mix[i] += (float)(weights[v] * vectors[v][i]);
            }
        }

        var norm = MathF.Sqrt(mix.Sum(x => x * x));
        return norm > 0 ? mix.Select(x => x / norm).ToArray() : mix;
    }

    public static double Distance(float[] a, float[] b)
    {
        var dot = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return 1 - dot;
    }

    public static double Score(IReadOnlyList<double> distances, IReadOnlyList<double> weights) =>
        distances.Select((distance, i) => distances.Count * weights[i] * distance).Max();

    /// <summary>
    /// The best <paramref name="max"/> candidates, best first, each with its distance to every show of the mix.
    /// </summary>
    public static List<MixRank> Rank(IEnumerable<(Guid ShowId, float[] Vector)> candidates, IReadOnlyList<float[]> vectors,
        IReadOnlyList<double> weights, int max) =>
        candidates
            .Select(c =>
            {
                var distances = vectors.Select(v => Distance(c.Vector, v)).ToList();
                return new MixRank(c.ShowId, distances, Score(distances, weights));
            })
            .OrderBy(r => r.Score)
            .ThenBy(r => r.ShowId)
            .Take(max)
            .ToList();

    /// <summary>
    /// The shows have little in common: there is no match, or even the best is farther from one of them than
    /// <paramref name="maxDistance"/>, search's cutoff for "similar" (<see cref="EmbeddingModel.MaxDistance"/>).
    /// </summary>
    public static bool Loose(IReadOnlyList<MixRank> ranked, double maxDistance) => ranked.Count == 0 || ranked[0].Score > maxDistance;

    /// <summary>
    /// A distance (or a score) as closeness, 0-100: 100 is the same meaning.
    /// </summary>
    public static int Percent(double distance) => (int)Math.Round(100 * Math.Clamp(1 - distance, 0, 1));

    /// <summary>
    /// Where a show sits between two others, as a percentage like the first: closer to it means a bigger share.
    /// </summary>
    public static int Share(double toFirst, double toSecond) => (int)Math.Round(100 * toSecond / Math.Max(toFirst + toSecond, 1e-6));

    /// <summary>
    /// A candidate of another kind (scripted, animation, reality, …) than any of the mix's shows is left out. One
    /// with no known kind stays, and so does every candidate when none of the mix's shows has a kind.
    /// </summary>
    public static bool FitsKinds(IReadOnlySet<string> kinds, string? kind) => kinds.Count == 0 || kind is null || kinds.Contains(kind);
}

/// <param name="Distances">Its distance to each show of the mix, in the mix's order.</param>
/// <param name="Score">See <see cref="MixRules"/>; lower is better.</param>
public sealed record MixRank(Guid ShowId, IReadOnlyList<double> Distances, double Score);
