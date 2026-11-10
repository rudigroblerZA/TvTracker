using Recommendations.API.Data;
using Recommendations.API.Services;
using TvTracker.IntegrationEvents;

namespace Recommendations.UnitTests;

/// <summary>
/// How two shows are matched: content similarity plus a nanogenre bonus, scaled by DNA
/// affinity between 0.5 and 1, so DNA reorders shows that are already alike and never makes an unrelated show similar.
/// </summary>
public class MatchTests
{
    private static ShowFeatures Show(string title, string[] genres, string[] keywords, params Guid[] similar) => new()
    {
        ShowId = Guid.NewGuid(),
        Title = title,
        Genres = [.. genres],
        Keywords = [.. keywords],
        SimilarShowIds = [.. similar]
    };

    /// <summary>
    /// Every trait at the typical show's value, so none is distinctive, except those given.
    /// </summary>
    private static Dictionary<string, double> Dna(params (string Key, double Value)[] overrides)
    {
        var dna = DnaTraits.All.ToDictionary(t => t.Key, t => t.Typical);
        foreach (var (key, value) in overrides)
        {
            dna[key] = value;
        }
        return dna;
    }

    private static Dictionary<string, double> Every(double value) => DnaTraits.All.ToDictionary(t => t.Key, _ => value);

    private static RecommendationEngine.Extras Extras(params (ShowFeatures Show, Dictionary<string, double>? Dna, string[]? Nanogenres)[] shows) => new(
        shows.Where(s => s.Dna is not null).ToDictionary(s => s.Show.ShowId, s => s.Dna!),
        shows.Where(s => s.Nanogenres is not null).ToDictionary(s => s.Show.ShowId, s => s.Nanogenres!.ToList()));

    private static readonly string[] Comedy = ["Comedy"];
    private static readonly string[] ScriptedEnglish = ["scripted", "english"];

    // Same genres and keywords, not on each other's provider lists: content 0.3 × 1 + 0.2 × 1 = 0.5. xUnit makes a new
    // instance per test, so each test gets its own pair.
    private readonly ShowFeatures a = Show("A", Comedy, ScriptedEnglish);
    private readonly ShowFeatures b = Show("B", Comedy, ScriptedEnglish);

    [Fact]
    public void Without_DNA_the_affinity_is_middling()
    {
        var match = RecommendationEngine.Match(a, b, Extras());

        Assert.Equal(0.5 * 0.75, match.Score, 6);
        Assert.False(match.DnaLed);
    }

    [Fact]
    public void DNA_scales_the_content_score_from_half_to_whole()
    {
        // Identical DNA: similarity 1, full affinity. Its traits are all typical (hour-long, weekly, recent), which says
        // nothing, so it makes no DNA reason.
        var identical = RecommendationEngine.Match(a, b, Extras((a, Dna(), null), (b, Dna(), null)));
        Assert.Equal(0.5, identical.Score, 6);
        Assert.False(identical.DnaLed);

        // Similarity 0.8 rescales to an affinity of 0.5 (0.65 → 0, 0.95 → 1).
        Assert.Equal(0.5 * 0.75, RecommendationEngine.Match(a, b, Extras((a, Every(0.4), null), (b, Every(0.6), null))).Score, 6);

        // 0.65 or less is no affinity: half the content score, never less.
        Assert.Equal(0.25, RecommendationEngine.Match(a, b, Extras((a, Every(0.1), null), (b, Every(0.9), null))).Score, 6);
    }

    [Fact]
    public void DNA_never_makes_a_show_about_something_else_similar()
    {
        var other = Show("B", ["Crime"], ["documentary"]);

        Assert.Equal(0, RecommendationEngine.Match(a, other, Extras((a, Dna(), null), (other, Dna(), null))).Score);
    }

    [Fact]
    public void Another_kind_of_show_weakens_the_affinity_most_and_another_language_a_little()
    {
        var reality = Show("B", Comedy, ["reality", "english"]);
        var korean = Show("C", Comedy, ["scripted", "korean"]);
        // Keywords overlap a third: content 0.3 + 0.2 / 3.
        var content = 0.3 + 0.2 / 3;

        Assert.Equal(content * (0.5 + 0.5 * 0.4),
            RecommendationEngine.Match(a, reality, Extras((a, Dna(), null), (reality, Dna(), null))).Score, 6);
        Assert.Equal(content * (0.5 + 0.5 * 0.8),
            RecommendationEngine.Match(a, korean, Extras((a, Dna(), null), (korean, Dna(), null))).Score, 6);
    }

    [Fact]
    public void A_close_DNA_names_the_distinctive_traits_both_share()
    {
        var dna = Dna((DnaTraits.Release, 1));

        var match = RecommendationEngine.Match(a, b, Extras((a, dna, null), (b, dna, null)));

        Assert.True(match.DnaLed);
        Assert.Equal("drops all at once", match.SharedTraits);
    }

    [Fact]
    public void A_shared_nanogenre_is_a_bonus_on_top_of_content()
    {
        var match = RecommendationEngine.Match(a, b, Extras((a, null, ["Sports comedy"]), (b, null, ["Sports comedy"])));

        Assert.Equal((0.5 + 0.25) * 0.75, match.Score, 6);
        Assert.Equal("Sports comedy", match.SharedNanogenre);
    }

    [Fact]
    public void A_pair_in_no_nanogenre_loses_nothing()
    {
        Assert.Equal(RecommendationEngine.Match(a, b, Extras()).Score,
            RecommendationEngine.Match(a, b, Extras((a, null, ["Sports comedy"]), (b, null, ["Medical drama"]))).Score, 6);
    }

    [Fact]
    public void The_providers_similar_shows_count_by_rank()
    {
        var first = Guid.NewGuid();
        var third = Guid.NewGuid();
        var show = Show("A", [], [], first, Guid.NewGuid(), third);

        Assert.Equal(0.4, RecommendationEngine.Similarity(show, new ShowFeatures { ShowId = first, Title = "First" }), 6);
        Assert.Equal(0.4 * 0.8, RecommendationEngine.Similarity(show, new ShowFeatures { ShowId = third, Title = "Third" }), 6);
    }

    [Fact]
    public void Jaccard_ignores_case_and_empty_sets()
    {
        Assert.Equal(1.0 / 3, RecommendationEngine.Jaccard(["Drama", "Crime"], ["crime", "Thriller"]), 6);
        Assert.Equal(0, RecommendationEngine.Jaccard([], ["Drama"]));
    }

    [Fact]
    public void Reasons_say_what_the_shows_share()
    {
        var candidate = Show("B", ["Drama", "Crime"], []);
        var target = Show("Breaking Bad", ["Crime", "Drama", "Thriller"], [], candidate.ShowId);

        Assert.Equal("Often watched with Breaking Bad", RecommendationEngine.ReasonFor(target, candidate));
        Assert.Equal("Also drama and crime", RecommendationEngine.ReasonFor(Show("X", ["Crime", "Drama"], []), candidate));
        Assert.Equal("Similar themes", RecommendationEngine.ReasonFor(Show("Y", ["Comedy"], []), candidate));
    }
}
