using Catalog.Data;
using Catalog.Embeddings;
using Catalog.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Catalog.IntegrationTests;

/// <summary>
/// Search through <see cref="ShowSearch"/>, against a real catalog: title matches first, then near-spellings, then
/// shows similar in meaning, and how the three fill pages. The model is scripted: a term's vector is whatever the test
/// says it is.
/// </summary>
public sealed class ShowSearchTests(CatalogDatabase database) : IClassFixture<CatalogDatabase>, IAsyncLifetime
{
    private static readonly EmbeddingModel Model = new() { Id = "test-model", MaxDistance = 0.5 };

    private static readonly Guid SpaceStation = Guid.NewGuid(), Spaced = Guid.NewGuid(), LostAmongTheStars = Guid.NewGuid(),
        CastawaysInOrbit = Guid.NewGuid(), BakingChampionship = Guid.NewGuid(), BreakingBad = Guid.NewGuid(), NotEmbedded = Guid.NewGuid();

    // Meanings: space along the first axis, baking along the second, crime along the third.
    private static readonly float[] Space = [1, 0, 0], Baking = [0, 1, 0], Crime = [0, 0, 1];

    private readonly ScriptedModel model = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = database.Context();
        if (await db.Shows.AnyAsync(Ct))
        {
            return;
        }

        Add(db, SpaceStation, "Space Station", popularity: 50, Baking);
        Add(db, Spaced, "Spaced", popularity: 90, Baking, genre: "Comedy");
        Add(db, LostAmongTheStars, "Lost Among the Stars", popularity: 10, [1, 0.3f, 0]);
        Add(db, CastawaysInOrbit, "Castaways in Orbit", popularity: 80, Space, genre: "Comedy");
        Add(db, BakingChampionship, "Baking Championship", popularity: 70, Baking);
        Add(db, BreakingBad, "Breaking Bad", popularity: 99, Crime);
        Add(db, NotEmbedded, "Stranded Beyond Mars", popularity: 60, null);
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Title_matches_come_first_then_shows_similar_in_meaning_closest_first()
    {
        model.Means("space", Space);

        var found = await SearchAsync("space");

        // Both titles have a word starting with the term, so the more popular leads; then by closeness, not popularity.
        Assert.Equal([Spaced, SpaceStation, CastawaysInOrbit, LostAmongTheStars], Ids(found));
        Assert.Equal((4, 2), (found.Total, found.TitleCount));
    }

    [Fact]
    public async Task A_show_farther_than_the_models_cutoff_is_not_a_match()
    {
        model.Means("stranded in space", Space);

        var found = await SearchAsync("stranded in space");

        Assert.Equal([CastawaysInOrbit, LostAmongTheStars], Ids(found));
        Assert.Equal((2, 0), (found.Total, found.TitleCount));
    }

    [Fact]
    public async Task A_show_with_no_vector_is_found_by_title_only()
    {
        model.Means("mars", Space);

        var found = await SearchAsync("mars");

        Assert.Equal(NotEmbedded, found.Shows[0].Id);
        Assert.Equal(1, found.TitleCount);
        Assert.DoesNotContain(NotEmbedded, Ids(found).Skip(1));
    }

    [Fact]
    public async Task A_misspelled_title_is_found_and_counted_as_a_title_match_once()
    {
        // Its meaning matches too: the show must not be listed a second time.
        model.Means("breakng bad", Crime);

        var found = await SearchAsync("breakng bad");

        Assert.Equal([BreakingBad], Ids(found));
        Assert.Equal((1, 1), (found.Total, found.TitleCount));
    }

    [Fact]
    public async Task Pages_run_from_the_title_matches_into_the_similar_shows()
    {
        model.Means("space", Space);

        var first = await SearchAsync("space", page: 1, pageSize: 3);
        var second = await SearchAsync("space", page: 2, pageSize: 3);
        var third = await SearchAsync("space", page: 3, pageSize: 3);

        Assert.Equal([Spaced, SpaceStation, CastawaysInOrbit], Ids(first));
        Assert.Equal([LostAmongTheStars], Ids(second));
        Assert.Empty(third.Shows);
        Assert.All([first, second, third], p => Assert.Equal((4, 2), (p.Total, p.TitleCount)));
    }

    [Fact]
    public async Task While_the_model_is_unavailable_search_answers_with_title_matches_only()
    {
        model.Unavailable = true;

        var found = await SearchAsync("space");

        Assert.Equal([Spaced, SpaceStation], Ids(found));
        Assert.Equal((2, 2), (found.Total, found.TitleCount));
    }

    [Fact]
    public async Task A_term_too_short_to_carry_meaning_is_not_embedded()
    {
        var found = await SearchAsync("sp");

        Assert.Equal([Spaced, SpaceStation], Ids(found));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task The_term_is_embedded_as_a_search_term_not_as_a_document()
    {
        model.Means("space", Space);

        await SearchAsync("space");

        Assert.Equal(EmbeddingInput.Query, model.LastInputType);
    }

    [Fact]
    public async Task Only_the_shows_handed_in_are_searched_by_title_and_by_meaning()
    {
        model.Means("space", Space);
        await using var db = database.Context();

        var found = await Search().PageAsync(db, db.Shows.AsNoTracking().Where(s => s.Genres.Contains("Comedy")), "space", 1, 20, Ct);

        Assert.Equal([Spaced, CastawaysInOrbit], Ids(found));
    }

    [Fact]
    public async Task Without_a_term_shows_come_most_popular_first_and_nothing_is_embedded()
    {
        var found = await SearchAsync(null, pageSize: 3);

        Assert.Equal([BreakingBad, Spaced, CastawaysInOrbit], Ids(found));
        Assert.Equal((7, null), (found.Total, found.TitleCount));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Suggestions_are_by_title_and_near_spelling_never_by_meaning()
    {
        model.Means("space", Space);
        await using var db = database.Context();
        var all = db.Shows.AsNoTracking();

        Assert.Equal([Spaced, SpaceStation], (await ShowSearch.SuggestAsync(all, "space", Ct)).Select(s => s.Id));
        Assert.Equal([BreakingBad], (await ShowSearch.SuggestAsync(all, "breakng bad", Ct)).Select(s => s.Id));
        Assert.Empty(await ShowSearch.SuggestAsync(all, "s", Ct));
        Assert.Equal(0, model.Calls);
    }

    private ShowSearch Search() => new(new SemanticSearch(model, Model, NullLogger<SemanticSearch>.Instance));

    private async Task<ShowSearchPage> SearchAsync(string? term, int page = 1, int pageSize = 20)
    {
        await using var db = database.Context();
        return await Search().PageAsync(db, db.Shows.AsNoTracking(), term, page, pageSize, Ct);
    }

    private static List<Guid> Ids(ShowSearchPage page) => page.Shows.Select(s => s.Id).ToList();

    private static void Add(CatalogContext db, Guid id, string title, double popularity, float[]? meaning, string? genre = null)
    {
        db.Shows.Add(new Show { Id = id, Source = "test", ExternalId = id.ToString("N"), Title = title, Popularity = popularity, Genres = genre is null ? [] : [genre] });
        if (meaning is not null)
        {
            db.ShowEmbeddings.Add(new ShowEmbedding { ShowId = id, Model = Model.Id, Embedding = new Vector(Unit(meaning)), SourceHash = "test", EmbeddedAtUtc = DateTime.UtcNow });
        }
    }

    private static float[] Unit(float[] direction)
    {
        var vector = new float[ShowEmbedding.Dimensions];
        var norm = MathF.Sqrt(direction.Sum(x => x * x));
        for (var i = 0; i < direction.Length; i++)
        {
            vector[i] = direction[i] / norm;
        }
        return vector;
    }

    /// <summary>
    /// A model that says what the test told it a term means; a term it wasn't told about means nothing any show does.
    /// </summary>
    private sealed class ScriptedModel : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Dictionary<string, float[]> meanings = [];

        public bool Unavailable { get; set; }
        public int Calls { get; private set; }
        public object? LastInputType { get; private set; }

        public void Means(string term, float[] direction) => meanings[term] = Unit(direction);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastInputType = options?.AdditionalProperties?.GetValueOrDefault(EmbeddingInput.Key);
            if (Unavailable)
            {
                throw new EmbeddingsUnavailableException("The model is down.");
            }
            // The last axis: no show's vector has anything there.
            var nothing = new float[ShowEmbedding.Dimensions];
            nothing[^1] = 1;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(v => new Embedding<float>(meanings.GetValueOrDefault(v, nothing)))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
