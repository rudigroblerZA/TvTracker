using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Mixes;

/// <summary>
/// One person's side of a taste match, as the catalog sees it: shows and how much each counts. No person is named.
/// </summary>
/// <param name="Loved">The shows that are compared, each with its weight.</param>
/// <param name="Known">Every show the person knows, loved or not: never suggested.</param>
public sealed record TasteSide(IReadOnlyDictionary<Guid, double> Loved, IReadOnlySet<Guid> Known);

/// <param name="Alike">How close the two sides are in meaning, 0-1; null when a side has too few shows the catalog can compare.</param>
/// <param name="UsableFirst">The first side's loved shows that have a vector.</param>
/// <param name="Together">Shows neither knows that sit between the two, best first.</param>
/// <param name="Loose">The sides have little in common: <paramref name="Together"/> is the middle ground, not close matches.</param>
public sealed record TasteComparison(double? Alike, int UsableFirst, int UsableSecond, IReadOnlyList<Guid> Together, bool Loose);

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
    /// A distance as closeness for this purpose: 1 for the same show, 0 from the model's cutoff for "similar"
    /// (<see cref="EmbeddingModel.MaxDistance"/>) outwards.
    /// </summary>
    public static double Closeness(double distance, double maxDistance) => Math.Clamp(1 - distance / maxDistance, 0, 1);

    /// <summary>
    /// How alike two sides are, 0-1: for each show of one side, its closeness to the nearest show of the other, averaged
    /// by weight; then the same the other way; then the mean of the two. So it is the same whichever side is first, and a
    /// side of three is compared as fairly as a side of fifty.
    /// </summary>
    public static double Alike(IReadOnlyList<(float[] Vector, double Weight)> first, IReadOnlyList<(float[] Vector, double Weight)> second, double maxDistance)
    {
        static double Towards(IReadOnlyList<(float[] Vector, double Weight)> from, IReadOnlyList<(float[] Vector, double Weight)> to, double maxDistance)
        {
            var total = from.Sum(f => f.Weight);
            return total > 0
                ? from.Sum(f => f.Weight * Closeness(to.Min(t => MixRules.Distance(f.Vector, t.Vector)), maxDistance)) / total
                : 0;
        }

        return first.Count == 0 || second.Count == 0 ? 0 : (Towards(first, second, maxDistance) + Towards(second, first, maxDistance)) / 2;
    }

    /// <summary>
    /// The shows a mix of the two sides is seeded with, and their weights: up to <paramref name="perSide"/> of each side's
    /// heaviest, each side counting half whatever its size. A show both sides seed counts for both.
    /// </summary>
    public static (List<Guid> ShowIds, List<double> Weights) Seeds(IReadOnlyDictionary<Guid, double> first, IReadOnlyDictionary<Guid, double> second, int perSide)
    {
        static List<Guid> Heaviest(IReadOnlyDictionary<Guid, double> side, int count) =>
            side.OrderByDescending(l => l.Value).ThenBy(l => l.Key).Take(count).Select(l => l.Key).ToList();

        var (a, b) = (Heaviest(first, perSide), Heaviest(second, perSide));
        var weights = new Dictionary<Guid, double>();
        foreach (var (side, ids) in new[] { (a, a), (b, b) })
        {
            foreach (var id in ids)
            {
                weights[id] = weights.GetValueOrDefault(id) + 0.5 / side.Count;
            }
        }
        var order = a.Concat(b).Distinct().ToList();
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
    /// How many shows the mix is asked for before the ones either side knows are left out.
    /// </summary>
    private const int MixedBeforeKnown = 40;

    /// <param name="seedsPerSide">How many of each side's heaviest shows seed the suggestions.</param>
    /// <param name="maxTogether">The most suggestions to return.</param>
    public static async Task<TasteComparison> CompareAsync(CatalogContext db, EmbeddingModel model, TasteSide first, TasteSide second,
        int seedsPerSide, int maxTogether, CancellationToken ct)
    {
        var ids = first.Loved.Keys.Concat(second.Loved.Keys).Distinct().ToList();
        var vectors = await db.ShowEmbeddings.AsNoTracking()
            .Where(e => ids.Contains(e.ShowId) && e.Model == model.Id)
            .ToDictionaryAsync(e => e.ShowId, e => e.Embedding.ToArray(), ct);

        Dictionary<Guid, double> Usable(TasteSide side) => side.Loved.Where(l => vectors.ContainsKey(l.Key)).ToDictionary(l => l.Key, l => l.Value);
        var (a, b) = (Usable(first), Usable(second));
        if (a.Count < TasteRules.MinUsable || b.Count < TasteRules.MinUsable)
        {
            return new TasteComparison(null, a.Count, b.Count, [], false);
        }

        var alike = TasteRules.Alike(
            a.Select(l => (vectors[l.Key], l.Value)).ToList(), b.Select(l => (vectors[l.Key], l.Value)).ToList(), model.MaxDistance);

        var (seeds, weights) = TasteRules.Seeds(a, b, seedsPerSide);
        var mix = await ShowMixer.MixAsync(db, model, seeds, weights, MixedBeforeKnown, ct);
        var together = (mix?.Matches ?? [])
            .Select(m => m.Show.Id)
            .Where(id => !first.Known.Contains(id) && !second.Known.Contains(id) && !first.Loved.ContainsKey(id) && !second.Loved.ContainsKey(id))
            .Take(maxTogether)
            .ToList();
        return new TasteComparison(alike, a.Count, b.Count, together, mix?.Loose ?? false);
    }
}
