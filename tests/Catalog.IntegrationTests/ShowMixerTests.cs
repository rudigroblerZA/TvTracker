using Catalog.Mixes;
using Catalog.Data;
using Catalog.Embeddings;
using Pgvector;

namespace Catalog.IntegrationTests;

/// <summary>
/// A mix against a real catalog: which shows are candidates at all (the vector index, the exclusions, the kinds) and
/// what comes back when a show isn't there or isn't embedded yet. The ranking itself is <c>MixRulesTests</c>'.
/// </summary>
public sealed class ShowMixerTests(CatalogDatabase database) : IClassFixture<CatalogDatabase>, IAsyncLifetime
{
    private static readonly EmbeddingModel Model = new() { Id = "test-model", MaxDistance = 0.5 };

    private static readonly Guid Crime = Guid.NewGuid(), Sitcom = Guid.NewGuid(), CrimeComedy = Guid.NewGuid(), CrimeSpinOff = Guid.NewGuid(),
        SitcomRemake = Guid.NewGuid(), DocumentaryInBetween = Guid.NewGuid(), UnknownKindInBetween = Guid.NewGuid(), Nature = Guid.NewGuid(),
        OtherModel = Guid.NewGuid(), JustImported = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Crime along the first axis, sitcom along the second, nature along the third.
    public async ValueTask InitializeAsync()
    {
        await using var db = database.Context();
        if (db.Shows.Any())
        {
            return;
        }

        Add(db, Crime, "Crime Drama", "scripted", [1, 0, 0]);
        Add(db, Sitcom, "Office Sitcom", "scripted", [0, 1, 0]);
        Add(db, CrimeComedy, "Crime Comedy", "scripted", [1, 1, 0]);
        Add(db, CrimeSpinOff, "Crime Drama: Miami", "scripted", [1, 0.05f, 0]);
        Add(db, SitcomRemake, "Office Sitcom", "scripted", [0.05f, 1, 0]);
        Add(db, DocumentaryInBetween, "Cops Do Stand-Up: The Documentary", "documentary", [1, 1, 0.05f]);
        Add(db, UnknownKindInBetween, "Untagged Caper", null, [1, 0.8f, 0]);
        Add(db, Nature, "Blue Planet", "documentary", [0, 0, 1]);
        Add(db, OtherModel, "Embedded By Another Model", "scripted", [1, 1, 0], model: "another-model");
        Add(db, JustImported, "Just Imported", "scripted", null);
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_blend_puts_the_show_between_both_first_and_the_spin_off_of_one_last()
    {
        var mix = await MixAsync([Crime, Sitcom]);

        Assert.True(mix!.Indexed);
        Assert.Equal([Crime, Sitcom], mix.Inputs.Select(s => s.Id));
        Assert.Equal(CrimeComedy, mix.Matches[0].Show.Id);
        Assert.False(mix.Loose);
        var spinOff = mix.Matches.Single(m => m.Show.Id == CrimeSpinOff);
        Assert.True(spinOff.Score > mix.Matches[0].Score + 0.5);
        Assert.True(spinOff.Distances[0] < 0.01 && spinOff.Distances[1] > 0.9);
    }

    [Fact]
    public async Task The_mixs_own_shows_and_shows_titled_like_them_are_never_matches()
    {
        var mix = await MixAsync([Crime, Sitcom]);

        var ids = mix!.Matches.Select(m => m.Show.Id).ToList();
        Assert.DoesNotContain(Crime, ids);
        Assert.DoesNotContain(Sitcom, ids);
        Assert.DoesNotContain(SitcomRemake, ids);
    }

    [Fact]
    public async Task A_show_of_another_kind_is_left_out_and_one_of_no_known_kind_stays()
    {
        var mix = await MixAsync([Crime, Sitcom]);

        var ids = mix!.Matches.Select(m => m.Show.Id).ToList();
        Assert.DoesNotContain(DocumentaryInBetween, ids);
        Assert.DoesNotContain(Nature, ids);
        Assert.Contains(UnknownKindInBetween, ids);
    }

    [Fact]
    public async Task Mixing_in_a_show_of_another_kind_lets_that_kind_in()
    {
        var mix = await MixAsync([Crime, Sitcom, Nature]);

        Assert.Contains(DocumentaryInBetween, mix!.Matches.Select(m => m.Show.Id));
        Assert.All(mix.Matches, m => Assert.Equal(3, m.Distances.Count));
        // Nothing is like a crime drama, a sitcom and a nature documentary at once.
        Assert.True(mix.Loose);
    }

    [Fact]
    public async Task Vectors_of_another_model_are_not_compared()
    {
        var mix = await MixAsync([Crime, Sitcom]);

        Assert.DoesNotContain(OtherModel, mix!.Matches.Select(m => m.Show.Id));
    }

    [Fact]
    public async Task A_lean_changes_the_order()
    {
        await using var db = database.Context();

        var towardsCrime = await ShowMixer.MixAsync(db, Model, [Crime, Sitcom], MixRules.LeanWeights(80), 20, Ct);
        var towardsSitcom = await ShowMixer.MixAsync(db, Model, [Crime, Sitcom], MixRules.LeanWeights(20), 20, Ct);

        // The untagged caper is more crime than comedy, so it fits better when the blend leans to crime.
        Assert.True(Score(towardsCrime!, UnknownKindInBetween) < Score(towardsSitcom!, UnknownKindInBetween));
    }

    [Fact]
    public async Task A_show_not_embedded_yet_gives_an_unindexed_answer_with_no_matches()
    {
        var mix = await MixAsync([Crime, JustImported]);

        Assert.False(mix!.Indexed);
        Assert.Empty(mix.Matches);
        Assert.Equal([Crime, JustImported], mix.Inputs.Select(s => s.Id));
    }

    [Fact]
    public async Task A_show_the_catalog_doesnt_have_gives_no_answer() =>
        Assert.Null(await MixAsync([Crime, Guid.NewGuid()]));

    [Fact]
    public async Task No_more_matches_come_back_than_asked_for()
    {
        await using var db = database.Context();

        var mix = await ShowMixer.MixAsync(db, Model, [Crime, Sitcom], MixRules.EvenWeights(2), 2, Ct);

        Assert.Equal(2, mix!.Matches.Count);
    }

    private async Task<MixOutcome?> MixAsync(Guid[] showIds)
    {
        await using var db = database.Context();
        return await ShowMixer.MixAsync(db, Model, showIds, MixRules.EvenWeights(showIds.Length), 20, Ct);
    }

    private static double Score(MixOutcome mix, Guid id) => mix.Matches.Single(m => m.Show.Id == id).Score;

    private static void Add(CatalogContext db, Guid id, string title, string? kind, float[]? direction, string? model = null)
    {
        db.Shows.Add(new Show { Id = id, Source = "test", ExternalId = id.ToString("N"), Title = title, Keywords = kind is null ? [] : [kind] });
        if (direction is null)
        {
            return;
        }

        var vector = new float[ShowEmbedding.Dimensions];
        var norm = MathF.Sqrt(direction.Sum(x => x * x));
        for (var i = 0; i < direction.Length; i++)
        {
            vector[i] = direction[i] / norm;
        }
        db.ShowEmbeddings.Add(new ShowEmbedding { ShowId = id, Model = model ?? Model.Id, Embedding = new Vector(vector), SourceHash = "test", EmbeddedAtUtc = DateTime.UtcNow });
    }
}
