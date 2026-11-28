using Catalog.Mixes;

namespace Catalog.UnitTests;

/// <summary>
/// Comparing two tastes by meaning: the same show is fully alike, shows past the model's cutoff aren't alike at all,
/// the answer is the same whichever side is first, and each side seeds half the suggestions whatever its size.
/// </summary>
public class TasteRulesTests
{
    private static readonly float[] Crime = [1, 0, 0], Comedy = [0, 1, 0], CrimeComedy = Unit(1, 1, 0);

    [Fact]
    public void The_same_shows_are_fully_alike_and_unrelated_ones_not_at_all()
    {
        Assert.Equal(1, TasteRules.Alike([(Crime, 1), (Comedy, 1)], [(Comedy, 0.5), (Crime, 1)], 0.5), 6);
        Assert.Equal(0, TasteRules.Alike([(Crime, 1)], [(Comedy, 1)], 0.5), 6);
        Assert.Equal(0, TasteRules.Alike([], [(Comedy, 1)], 0.5));
    }

    [Fact]
    public void Closeness_runs_from_the_same_show_to_how_far_apart_unrelated_shows_are()
    {
        Assert.Equal(1, TasteRules.Closeness(0, 0.46));
        Assert.Equal(0.5, TasteRules.Closeness(0.23, 0.46), 6);
        Assert.Equal(0, TasteRules.Closeness(0.46, 0.46));
        Assert.Equal(0, TasteRules.Closeness(0.9, 0.46));
        Assert.Equal(0, TasteRules.Closeness(0.2, 0));
    }

    [Fact]
    public void The_reference_is_the_median_distance_between_pairs()
    {
        // Three pairs: crime to comedy 1, and each to the crime comedy about 0.29.
        Assert.Equal(0.293, TasteRules.ReferenceDistance([Crime, Comedy, CrimeComedy]), 3);
        Assert.Equal(0, TasteRules.ReferenceDistance([Crime]));
    }

    [Fact]
    public void Alike_is_the_same_whichever_side_is_first_and_a_small_side_counts_as_much_as_a_large_one()
    {
        var small = new[] { (Crime, 1.0) };
        var large = new[] { (Crime, 1.0), (Comedy, 1.0), (CrimeComedy, 0.5) };

        var forwards = TasteRules.Alike(small, large, 0.5);

        Assert.Equal(forwards, TasteRules.Alike(large, small, 0.5), 6);
        // The small side is all matched (1); of the large side only the crime show is, plus a little of the crime comedy.
        Assert.True(forwards is > 0.6 and < 0.8, $"was {forwards}");
    }

    [Fact]
    public void A_show_counts_towards_alike_by_its_weight()
    {
        var mostlyCrime = new[] { (Crime, 1.0), (Comedy, 0.1) };
        var evenly = new[] { (Crime, 1.0), (Comedy, 1.0) };

        Assert.True(TasteRules.Alike(mostlyCrime, [(Crime, 1)], 0.5) > TasteRules.Alike(evenly, [(Crime, 1)], 0.5));
    }

    [Fact]
    public void Each_side_seeds_half_the_mix_whatever_its_size_and_a_show_both_seed_counts_for_both()
    {
        var a = Guid.NewGuid();
        var (b1, b2, b3, b4) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var second = TasteRules.Heaviest(new Dictionary<Guid, double> { [b4] = 0.1, [b2] = 0.9, [b1] = 1, [b3] = 0.8 }, 3);

        var (ids, weights) = TasteRules.Seeds([a], second);

        // The heaviest three, heaviest first: the fourth seeds nothing.
        Assert.Equal([b1, b2, b3], second);
        Assert.Equal([a, b1, b2, b3], ids);
        Assert.Equal(0.5, weights[0], 6);
        Assert.All(weights.Skip(1), w => Assert.Equal(0.5 / 3, w, 6));
        Assert.Equal(1, weights.Sum(), 6);

        var (shared, sharedWeights) = TasteRules.Seeds([a], [a, b1]);
        Assert.Equal([a, b1], shared);
        Assert.Equal([0.75, 0.25], sharedWeights);
    }

    [Fact]
    public void Shows_that_weigh_the_same_are_picked_the_same_way_every_time()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var side = ids.ToDictionary(id => id, _ => 0.5);

        Assert.Equal(ids.Order().Take(3), TasteRules.Heaviest(side, 3));
        Assert.Equal(TasteRules.Heaviest(side, 3), TasteRules.Heaviest(ids.AsEnumerable().Reverse().ToDictionary(id => id, _ => 0.5), 3));
    }

    private static float[] Unit(params float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }
}
