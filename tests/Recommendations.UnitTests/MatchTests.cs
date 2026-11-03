using Recommendations.API.Data;
using Recommendations.API.Services;
using TvTracker.IntegrationEvents;

namespace Recommendations.UnitTests;

/// <summary>
/// How two shows are matched (CONTEXT.md, Recommendations): content similarity plus a nanogenre bonus, scaled by DNA
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

    // Same genres and keywords, not on each other's provider lists: content 0.3 × 1 + 0.2 × 1 = 0.5.
    private static readonly string[] Comedy = ["Comedy"];
    private static readonly string[] ScriptedEnglish = ["scripted", "english"];

    [Fact]
    public void Without_DNA_the_affinity_is_middling()
    {
        var (a, b) = (Show("A", Comedy, ScriptedEnglish), Show("B", Comedy, ScriptedEnglish));
        var match = RecommendationEngine.Match(a, b, Extras());

        Assert.Equal(0.5 * 0.75, match.Score, 6);
        Assert.False(match.DnaLed);
    }

    [Fact]
    public void DNA_scales_the_content_score_from_half_to_whole()
    {
        var (a, b) = (Show("A", Comedy, ScriptedEnglish), Show("B", Comedy, ScriptedEnglish));

        // Identical DNA: similarity 1, full affinity.
        Assert.Equal(0.5, RecommendationEngine.Match(a, b, Extras((a, Dna(), null), (b, Dna(), null))).Score, 6);

        // Similarity 0.8 rescales to an affinity of 0.5 (0.65 → 0, 0.95 → 1).
        Assert.Equal(0.5 * 0.75, RecommendationEngine.Match(a, b, Extras((a, Every(0.4), null), (b, Every(0.6), null))).Score, 6);

        // 0.65 or less is no affinity: half the content score, never less.
        Assert.Equal(0.25, RecommendationEngine.Match(a, b, Extras((a, Every(0.1), null), (b, Every(0.9), null))).Score, 6);
    }

    [Fact]
    public void DNA_never_makes_a_show_about_something_else_similar()
    {
        var a = Show("A", Comedy, ScriptedEnglish);
        var b = Show("B", ["Crime"], ["documentary"]);

        Assert.Equal(0, RecommendationEngine.Match(a, b, Extras((a, Dna(), null), (b, Dna(), null))).Score);
    }

    [Fact]
    public void Another_kind_of_show_weakens_the_affinity_most_and_another_language_a_little()
    {
        var drama = Show("A", Comedy, ScriptedEnglish);
        var reality = Show("B", Comedy, ["reality", "english"]);
        var korean = Show("C", Comedy, ["scripted", "korean"]);
        // Keywords overlap a third: content 0.3 + 0.2 / 3.
        var content = 0.3 + 0.2 / 3;

        Assert.Equal(content * (0.5 + 0.5 * 0.4),
            RecommendationEngine.Match(drama, reality, Extras((drama, Dna(), null), (reality, Dna(), null))).Score, 6);
        Assert.Equal(content * (0.5 + 0.5 * 0.8),
            RecommendationEngine.Match(drama, korean, Extras((drama, Dna(), null), (korean, Dna(), null))).Score, 6);
    }

    [Fact]
    public void A_close_DNA_names_the_distinctive_traits_both_share()
    {
        var (a, b) = (Show("A", Comedy, ScriptedEnglish), Show("B", Comedy, ScriptedEnglish));
        var dna = Dna((DnaTraits.Release, 1));

        var match = RecommendationEngine.Match(a, b, Extras((a, dna, null), (b, dna, null)));

        Assert.True(match.DnaLed);
        Assert.Equal("drops all at once", match.SharedTraits);
    }

    [Fact]
    public void Typical_traits_alone_make_no_DNA_reason()
    {
        var (a, b) = (Show("A", Comedy, ScriptedEnglish), Show("B", Comedy, ScriptedEnglish));

        // Hour-long, weekly and recent are typical, so saying so tells nothing.
        Assert.False(RecommendationEngine.Match(a, b, Extras((a, Dna(), null), (b, Dna(), null))).DnaLed);
    }

    [Fact]
    public void A_shared_nanogenre_is_a_bonus_on_top_of_content()
    {
        var (a, b) = (Show("A", Comedy, ScriptedEnglish), Show("B", Comedy, ScriptedEnglish));
        var match = RecommendationEngine.Match(a, b, Extras((a, null, ["Sports comedy"]), (b, null, ["Sports comedy"])));

        Assert.Equal((0.5 + 0.25) * 0.75, match.Score, 6);
        Assert.Equal("Sports comedy", match.SharedNanogenre);
    }

    [Fact]
    public void A_pair_in_no_nanogenre_loses_nothing()
    {
        var (a, b) = (Show("A", Comedy, ScriptedEnglish), Show("B", Comedy, ScriptedEnglish));

        Assert.Equal(RecommendationEngine.Match(a, b, Extras()).Score,
            RecommendationEngine.Match(a, b, Extras((a, null, ["Sports comedy"]), (b, null, ["Medical drama"]))).Score, 6);
    }

    [Fact]
    public void The_providers_similar_shows_count_by_rank()
    {
        var first = Guid.NewGuid();
        var third = Guid.NewGuid();
        var a = Show("A", [], [], first, Guid.NewGuid(), third);

        Assert.Equal(0.4, RecommendationEngine.Similarity(a, new ShowFeatures { ShowId = first, Title = "First" }), 6);
        Assert.Equal(0.4 * 0.8, RecommendationEngine.Similarity(a, new ShowFeatures { ShowId = third, Title = "Third" }), 6);
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
        var b = Show("B", ["Drama", "Crime"], []);
        var a = Show("Breaking Bad", ["Crime", "Drama", "Thriller"], [], b.ShowId);

        Assert.Equal("Often watched with Breaking Bad", RecommendationEngine.ReasonFor(a, b));
        Assert.Equal("Also drama and crime", RecommendationEngine.ReasonFor(Show("X", ["Crime", "Drama"], []), b));
        Assert.Equal("Similar themes", RecommendationEngine.ReasonFor(Show("Y", ["Comedy"], []), b));
    }
}
