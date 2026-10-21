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
public sealed class SemanticSearch(
    IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingModel model, ILogger<SemanticSearch> logger)
{
    /// <summary>How many similar shows search adds at most.</summary>
    public const int MaxResults = 30;

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
            query = new Vector((await generator.GenerateAsync([term], EmbeddingInput.QueryOptions, ct))[0].Vector);
        }
        catch (EmbeddingsUnavailableException)
        {
            logger.LogDebug("Embedding model not loaded; searching titles only");
            return [];
        }

        return await candidates
            .Join(db.ShowEmbeddings.Where(e => e.Model == model.Id), s => s.Id, e => e.ShowId,
                (s, e) => new { s.Id, Distance = e.Embedding.CosineDistance(query) })
            .Where(x => x.Distance < model.MaxDistance)
            .OrderBy(x => x.Distance)
            .Take(MaxResults)
            .Select(x => x.Id)
            .ToListAsync(ct);
    }
}
