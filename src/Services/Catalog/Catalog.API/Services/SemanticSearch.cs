using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Catalog.API.Services;

/// <summary>
/// Finds shows whose title, genres and summary are close in meaning to a search term ("stranded in space",
/// "chefs competing"), using the vectors the sync worker keeps. Search lists these after the title matches.
/// </summary>
public sealed class SemanticSearch(IEmbeddingGenerator<string, Embedding<float>> generator, ILogger<SemanticSearch> logger)
{
    /// <summary>How many similar shows search adds at most.</summary>
    public const int MaxResults = 30;

    // Cosine distance (0 = same meaning, 1 = unrelated) beyond which a show isn't shown as similar.
    // Tuned on TVmaze shows: related ones land around 0.3-0.64 ("cooking competition" → The Great British Bake Off
    // at 0.62), unrelated ones from about 0.66.
    private const double MaxDistance = 0.65;

    // Shorter terms (and typos of them) don't carry enough meaning to compare.
    private const int MinTermLength = 3;

    /// <summary>
    /// The ids of shows similar to <paramref name="term"/> among <paramref name="candidates"/>, closest first.
    /// Empty when the term is too short or the model isn't loaded yet.
    /// </summary>
    public async Task<List<Guid>> FindSimilarAsync(IQueryable<Show> candidates, CatalogContext db, string term, CancellationToken ct)
    {
        term = term.Trim();
        if (term.Length < MinTermLength)
        {
            return [];
        }

        Vector query;
        try
        {
            query = new Vector((await generator.GenerateAsync([term], cancellationToken: ct))[0].Vector);
        }
        catch (EmbeddingsUnavailableException)
        {
            logger.LogDebug("Embedding model not loaded; searching titles only");
            return [];
        }

        return await candidates
            .Join(db.ShowEmbeddings.Where(e => e.Model == LocalEmbeddingGenerator.ModelId), s => s.Id, e => e.ShowId,
                (s, e) => new { s.Id, Distance = e.Embedding.CosineDistance(query) })
            .Where(x => x.Distance < MaxDistance)
            .OrderBy(x => x.Distance)
            .Take(MaxResults)
            .Select(x => x.Id)
            .ToListAsync(ct);
    }
}
