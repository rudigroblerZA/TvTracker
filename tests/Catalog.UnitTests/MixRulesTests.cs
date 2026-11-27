using Catalog.Mixes;

namespace Catalog.UnitTests;

/// <summary>
/// The rules Blend and Mixtape rank by: a match has to be like every show of the mix, a lean makes one show count
/// more, and a mix with nothing close says so.
/// </summary>
public class MixRulesTests
{
    // Unit vectors in a plane: A and B at right angles, so their distance is 1.
    private static readonly float[] A = [1, 0, 0];
    private static readonly float[] B = [0, 1, 0];
    private static readonly float[] Between = Unit(1, 1, 0);
    private static readonly float[] NearA = Unit(1, 0.1f, 0);
    private static readonly float[] NearB = Unit(0.1f, 1, 0);
    private static readonly float[] Elsewhere = [0, 0, 1];

    private static readonly Guid BetweenId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid NearAId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid NearBId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid ElsewhereId = Guid.Parse("00000000-0000-0000-0000-000000000004");

    private static readonly (Guid, float[])[] Candidates = [(NearAId, NearA), (ElsewhereId, Elsewhere), (BetweenId, Between), (NearBId, NearB)];

    [Fact]
    public void An_even_mix_puts_the_show_between_both_ahead_of_one_close_to_only_one()
    {
        var ranked = MixRules.Rank(Candidates, [A, B], MixRules.EvenWeights(2), 10);

        Assert.Equal(BetweenId, ranked[0].ShowId);
        // The spin-off of A is nearly A, and nearly as far from B as a show about something else entirely.
        Assert.True(ranked.Single(r => r.ShowId == NearAId).Score > 0.85);
        Assert.Equal(ElsewhereId, ranked[^1].ShowId);
    }

    [Fact]
    public void An_even_mixs_score_is_the_distance_to_the_farthest_show()
    {
        Assert.Equal(0.7, MixRules.Score([0.2, 0.7, 0.4], MixRules.EvenWeights(3)), 6);
        Assert.Equal(0.7, MixRules.Score([0.7, 0.2], MixRules.LeanWeights(50)), 6);
    }

    [Fact]
    public void Leaning_towards_a_show_brings_the_ones_near_it_forward()
    {
        var towardsA = MixRules.Rank(Candidates, [A, B], MixRules.LeanWeights(80), 10);
        var towardsB = MixRules.Rank(Candidates, [A, B], MixRules.LeanWeights(20), 10);

        Assert.True(Place(towardsA, NearAId) < Place(towardsA, NearBId));
        Assert.True(Place(towardsB, NearBId) < Place(towardsB, NearAId));
        // Distance from the favoured show costs four times as much as distance from the other.
        Assert.Equal(1.6 * 0.5, MixRules.Score([0.5, 0.1], MixRules.LeanWeights(80)), 6);
        Assert.Equal(0.4 * 0.9, MixRules.Score([0.1, 0.9], MixRules.LeanWeights(80)), 6);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(35, 0.35)]
    [InlineData(250, 1)]
    public void A_lean_is_the_first_shows_share_and_stays_within_bounds(int lean, double first)
    {
        var weights = MixRules.LeanWeights(lean);

        Assert.Equal(first, weights[0], 6);
        Assert.Equal(1, weights.Sum(), 6);
    }

    [Fact]
    public void The_mix_is_a_unit_vector_that_moves_with_the_weights()
    {
        var even = MixRules.Mix([A, B], MixRules.EvenWeights(2));
        var leaning = MixRules.Mix([A, B], MixRules.LeanWeights(80));

        Assert.Equal(1, even.Sum(x => x * x), 5);
        Assert.Equal(even[0], even[1], 5);
        Assert.Equal(1, leaning.Sum(x => x * x), 5);
        Assert.True(MixRules.Distance(leaning, A) < MixRules.Distance(leaning, B));
    }

    [Fact]
    public void Only_the_best_are_kept_and_ties_come_out_in_the_same_order_each_time()
    {
        var twins = new[] { (NearBId, Between), (BetweenId, Between), (NearAId, Between) };

        var ranked = MixRules.Rank(twins, [A, B], MixRules.EvenWeights(2), 2);

        Assert.Equal([BetweenId, NearAId], ranked.Select(r => r.ShowId));
    }

    [Fact]
    public void A_mix_is_loose_when_nothing_matches_or_the_best_match_is_past_the_cutoff()
    {
        var ranked = MixRules.Rank(Candidates, [A, B], MixRules.EvenWeights(2), 10);
        var best = ranked[0].Score;

        Assert.True(MixRules.Loose([], 0.5));
        Assert.True(MixRules.Loose(ranked, best - 0.01));
        Assert.False(MixRules.Loose(ranked, best));
    }

    [Fact]
    public void Closeness_is_a_percentage_within_bounds()
    {
        Assert.Equal(100, MixRules.Percent(0));
        Assert.Equal(54, MixRules.Percent(0.46));
        Assert.Equal(0, MixRules.Percent(1.3));
        Assert.Equal(100, MixRules.Percent(-0.001));
    }

    [Fact]
    public void A_shows_share_of_the_first_grows_as_it_gets_closer_to_it()
    {
        Assert.Equal(50, MixRules.Share(0.3, 0.3));
        Assert.Equal(75, MixRules.Share(0.2, 0.6));
        Assert.Equal(100, MixRules.Share(0, 0.5));
        // The same show as both (which the catalog never offers) doesn't divide by zero.
        Assert.Equal(0, MixRules.Share(0, 0));
    }

    [Fact]
    public void A_candidate_of_another_kind_is_left_out_and_one_of_no_known_kind_stays()
    {
        IReadOnlySet<string> scripted = new HashSet<string> { "scripted" };

        Assert.True(MixRules.FitsKinds(scripted, "scripted"));
        Assert.False(MixRules.FitsKinds(scripted, "reality"));
        Assert.True(MixRules.FitsKinds(scripted, null));
        Assert.True(MixRules.FitsKinds(new HashSet<string>(), "reality"));
        Assert.True(MixRules.FitsKinds(new HashSet<string> { "scripted", "animation" }, "animation"));
    }

    private static int Place(List<MixRank> ranked, Guid id) => ranked.FindIndex(r => r.ShowId == id);

    private static float[] Unit(params float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }
}
