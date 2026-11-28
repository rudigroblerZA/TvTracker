using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Mixes;

/// <param name="Alike">How close the two sides are in meaning, 0-1; null when a side has too few shows the catalog can compare.</param>
/// <param name="SeedsFirst">The first side's shows the suggestions were seeded with: what a suggestion can be said to take from that side.</param>
/// <param name="Together">Shows that sit between the two, best first. Neither side's compared shows are among them; the
/// caller leaves out what else either knows.</param>
/// <param name="Loose">The sides have little in common: <paramref name="Together"/> is the middle ground, not close matches.</param>
public sealed record TasteComparison(double? Alike, IReadOnlyList<Guid> SeedsFirst, IReadOnlyList<Guid> SeedsSecond, IReadOnlyList<Guid> Together, bool Loose);

/// <summary>
/// The maths of comparing two tastes by meaning. Pure: vectors and numbers only.
/// </summary>
public static class TasteRules
{
    /// <summary>
    /// A side needs this many shows with a vector to be compared.
    /// </summary>
    public const int MinUsable = 3;

    /// <summary>
    /// The most shows of a side that are compared; a caller sending more has made a mistake.
    /// </summary>
    public const int MaxLoved = 100;

    /// <summary>
    /// How many of each side's heaviest shows seed the suggestions.
    /// </summary>
    public const int SeedsPerSide = 3;

    /// <summary>
    /// How many suggestions are returned at most: more than anyone shows, since the caller drops the ones either
    /// person already knows.
    /// </summary>
    public const int MaxTogether = 40;

    /// <summary>
    /// A distance as closeness: 1 for the same show, 0 from <paramref name="reference"/> outwards, the distance between
    /// two shows picked at random (<see cref="ReferenceDistance"/>). Measured against the catalog and not a fixed
    /// number, since each model spreads its distances differently; search's cutoff for "similar" is far too strict
    /// here (two tastes are alike long before every show of one is a search match for a show of the other).
    /// </summary>
    public static double Closeness(double distance, double reference) => reference > 0 ? Math.Clamp(1 - distance / reference, 0, 1) : 0;

    /// <summary>
    /// The distance two unrelated shows are apart: the median over every pair of <paramref name="vectors"/>.
    /// </summary>
    public static double ReferenceDistance(IReadOnlyList<float[]> vectors)
    {
        var distances = new List<double>(vectors.Count * (vectors.Count - 1) / 2);
        for (var i = 0; i < vectors.Count; i++)
        {
            for (var j = i + 1; j < vectors.Count; j++)
            {
                distances.Add(Vectors.Distance(vectors[i], vectors[j]));
            }
        }
        return Vectors.Median(distances);
    }

    /// <param name="reference">See <see cref="Closeness"/>.</param>
    public static double Alike(IReadOnlyList<(float[] Vector, double Weight)> first, IReadOnlyList<(float[] Vector, double Weight)> second, double reference)
    {
        static double Towards(IReadOnlyList<(float[] Vector, double Weight)> from, IReadOnlyList<(float[] Vector, double Weight)> to, double reference)
        {
            var total = from.Sum(f => f.Weight);
            return total > 0
                ? from.Sum(f => f.Weight * Closeness(to.Min(t => Vectors.Distance(f.Vector, t.Vector)), reference)) / total
                : 0;
        }

        return first.Count == 0 || second.Count == 0 ? 0 : (Towards(first, second, reference) + Towards(second, first, reference)) / 2;
    }

    /// <summary>
    /// A side's heaviest shows, heaviest first; shows that weigh the same go by id, so the pick is the same every time.
    /// </summary>
    public static List<Guid> Heaviest(IReadOnlyDictionary<Guid, double> side, int count) =>
        side.OrderByDescending(l => l.Value).ThenBy(l => l.Key).Take(count).Select(l => l.Key).ToList();

    /// <summary>
    /// The shows a mix of the two sides is seeded with, and their weights: each side counts half whatever its size, and a
    /// show both sides seed counts for both.
    /// </summary>
    /// <param name="first">The first side's seeds (<see cref="Heaviest"/>).</param>
    public static (List<Guid> ShowIds, List<double> Weights) Seeds(IReadOnlyList<Guid> first, IReadOnlyList<Guid> second)
    {
        var weights = new Dictionary<Guid, double>();
        foreach (var side in new[] { first, second })
        {
            foreach (var id in side)
            {
                weights[id] = weights.GetValueOrDefault(id) + 0.5 / side.Count;
            }
        }
        var order = first.Concat(second).Distinct().ToList();
        return (order, order.Select(id => weights[id]).ToList());
    }
}

/// <summary>
/// Compares two tastes against the catalog's vectors, and finds what the two could watch together with the mix
/// engine (<see cref="ShowMixer"/>). It is given show ids and weights only.
/// </summary>
public static class TasteMatcher
{
    /// <summary>
    /// How many of the catalog's most popular shows the reference distance is measured over.
    /// </summary>
    private const int ReferenceShows = 200;

    /// <summary>
    /// The distance two unrelated shows are apart under the current model (<see cref="TasteRules.ReferenceDistance"/>),
    /// over the catalog's most popular shows. It moves only as the catalog or the model does, so callers keep it a while.
    /// </summary>
    public static async Task<double> ReferenceDistanceAsync(CatalogContext db, EmbeddingModel model, CancellationToken ct)
    {
        var vectors = await db.ShowEmbeddings.AsNoTracking()
            .Where(e => e.Model == model.Id)
            .Join(db.Shows, e => e.ShowId, s => s.Id, (e, s) => new { e.Embedding, s.Popularity, s.Id })
            .OrderByDescending(x => x.Popularity).ThenBy(x => x.Id)
            .Take(ReferenceShows)
            .Select(x => x.Embedding)
            .ToListAsync(ct);
        return TasteRules.ReferenceDistance(vectors.Select(v => v.ToArray()).ToList());
    }

    /// <param name="reference">The distance two unrelated shows are apart (<see cref="ReferenceDistanceAsync"/>).</param>
    /// <param name="first">One side: the shows that are compared, each with its weight.</param>
    public static async Task<TasteComparison> CompareAsync(CatalogContext db, EmbeddingModel model, double reference,
        IReadOnlyDictionary<Guid, double> first, IReadOnlyDictionary<Guid, double> second, CancellationToken ct)
    {
        var ids = first.Keys.Concat(second.Keys).Distinct().ToList();
        var vectors = await db.ShowEmbeddings.AsNoTracking()
            .Where(e => ids.Contains(e.ShowId) && e.Model == model.Id)
            .ToDictionaryAsync(e => e.ShowId, e => e.Embedding.ToArray(), ct);

        // Only shows with a vector can be compared, or seed anything.
        Dictionary<Guid, double> Usable(IReadOnlyDictionary<Guid, double> side) => side.Where(l => vectors.ContainsKey(l.Key)).ToDictionary(l => l.Key, l => l.Value);
        var (a, b) = (Usable(first), Usable(second));
        if (a.Count < TasteRules.MinUsable || b.Count < TasteRules.MinUsable)
        {
            return new TasteComparison(null, [], [], [], false);
        }

        var alike = TasteRules.Alike(
            a.Select(l => (vectors[l.Key], l.Value)).ToList(), b.Select(l => (vectors[l.Key], l.Value)).ToList(), reference);

        var (seedsFirst, seedsSecond) = (TasteRules.Heaviest(a, TasteRules.SeedsPerSide), TasteRules.Heaviest(b, TasteRules.SeedsPerSide));
        var (seeds, weights) = TasteRules.Seeds(seedsFirst, seedsSecond);
        var mix = await ShowMixer.MixAsync(db, model, seeds, weights, TasteRules.MaxTogether + first.Count + second.Count, ct);
        var together = (mix?.Matches ?? [])
            .Select(m => m.Show.Id)
            .Where(id => !first.ContainsKey(id) && !second.ContainsKey(id))
            .Take(TasteRules.MaxTogether)
            .ToList();
        return new TasteComparison(alike, seedsFirst, seedsSecond, together, mix?.Loose ?? false);
    }
}
