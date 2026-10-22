using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Lean">How much the blend leans towards <paramref name="A"/>, 0-100 (50 = halfway).</param>
/// <param name="Indexed">False while either show still waits to be embedded; <paramref name="Matches"/> is empty then.</param>
/// <param name="Loose">
/// The two have little in common: even the best match is farther from one of them than search's cutoff for "similar"
/// (the model's <see cref="EmbeddingModel.MaxDistance"/>). The matches are the closest middle ground, not close.
/// </param>
public record BlendDto(ShowSummaryDto A, ShowSummaryDto B, int Lean, bool Indexed, IReadOnlyList<BlendMatchDto> Matches, bool Loose = false);

/// <param name="Fit">How well it fits between the two, 0-100, from its (lean-weighted) distance to the farther one.</param>
/// <param name="LikeA">Where the show sits between the two, as a percentage like A (100 - LikeA is like B).</param>
/// <param name="GenresFromA">Genres it shares with A only.</param>
/// <param name="GenresFromB">Genres it shares with B only.</param>
/// <param name="GenresFromBoth">Genres all three have.</param>
public record BlendMatchDto(
    ShowSummaryDto Show, int Fit, int LikeA, IReadOnlyList<string> GenresFromA, IReadOnlyList<string> GenresFromB, IReadOnlyList<string> GenresFromBoth);

/// <summary>
/// Show blend: two shows in, the shows that sit between them out ("Breaking Bad × The Office"). It mixes the two
/// shows' meaning vectors (the same ones semantic search uses), takes the catalog's shows nearest the mix, and ranks
/// them by their distance to the farther of the two. A show only like one of them (an NCIS spinoff, blended with
/// Friends) ranks low, however close it is to that one.
/// </summary>
public static class BlendApi
{
    public const int MaxMatches = 20;

    // Candidates nearest the mixed vector, re-ranked below. pgvector's HNSW index returns at most ef_search (40 by
    // default) rows per query, so asking for more wouldn't add any.
    private const int Candidates = 40;

    public static void MapBlendApi(this RouteGroupBuilder api) => api.MapGet("/shows/blend", GetBlendAsync).RequireFeature(FeatureFlags.Blend);

    /// <param name="lean">0-100, how much like <paramref name="a"/> (default 50); 20-80 keeps both shows in the mix.</param>
    private static async Task<Results<Ok<BlendDto>, NotFound, ValidationProblem>> GetBlendAsync(
        Guid a, Guid b, CatalogContext db, EmbeddingModel model, int lean = 50, CancellationToken ct = default)
    {
        if (a == b)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["b"] = ["Pick two different shows."] });
        }
        lean = Math.Clamp(lean, 0, 100);

        var shows = await db.Shows.AsNoTracking().Where(s => s.Id == a || s.Id == b).ToDictionaryAsync(s => s.Id, ct);
        if (!shows.TryGetValue(a, out var showA) || !shows.TryGetValue(b, out var showB))
        {
            return TypedResults.NotFound();
        }

        var vectors = await db.ShowEmbeddings.AsNoTracking()
            .Where(e => (e.ShowId == a || e.ShowId == b) && e.Model == model.Id)
            .ToDictionaryAsync(e => e.ShowId, e => e.Embedding.ToArray(), ct);
        if (!vectors.TryGetValue(a, out var va) || !vectors.TryGetValue(b, out var vb))
        {
            // Just imported (e.g. found by search a moment ago): the sync worker embeds it within a minute.
            return TypedResults.Ok(new BlendDto(showA.ToSummary(), showB.ToSummary(), lean, false, []));
        }

        var weight = lean / 100f;
        var mix = new Vector(Normalize(va.Zip(vb, (x, y) => weight * x + (1 - weight) * y).ToArray()));
        Vector vectorA = new(va), vectorB = new(vb);

        // Shows with the same title as either (a remake, or the classic Doctor Who next to the new one) aren't a blend.
        var candidates = await db.ShowEmbeddings.AsNoTracking()
            .Where(e => e.Model == model.Id && e.ShowId != a && e.ShowId != b)
            .Where(e => db.Shows.Any(s => s.Id == e.ShowId && s.Title != showA.Title && s.Title != showB.Title))
            .OrderBy(e => e.Embedding.CosineDistance(mix))
            .Take(Candidates)
            .Select(e => new { e.ShowId, ToA = e.Embedding.CosineDistance(vectorA), ToB = e.Embedding.CosineDistance(vectorB) })
            .ToListAsync(ct);

        // The farther of the two distances, each weighted by the lean: at 50 a match must be close to both; leaning
        // towards A, closeness to B matters less.
        var ranked = candidates
            .Select(c => (c.ShowId, c.ToA, c.ToB, Score: Math.Max(2 * weight * c.ToA, 2 * (1 - weight) * c.ToB)))
            .OrderBy(c => c.Score)
            .Take(MaxMatches)
            .ToList();

        var ids = ranked.Select(r => r.ShowId).ToList();
        var matches = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        return TypedResults.Ok(new BlendDto(
            showA.ToSummary(),
            showB.ToSummary(),
            lean,
            true,
            ranked
                .Where(r => matches.ContainsKey(r.ShowId))
                .Select(r =>
                {
                    var show = matches[r.ShowId];
                    return new BlendMatchDto(
                        show.ToSummary(),
                        (int)Math.Round(100 * Math.Clamp(1 - r.Score, 0, 1)),
                        // Closer to A means a bigger share of A.
                        (int)Math.Round(100 * r.ToB / Math.Max(r.ToA + r.ToB, 1e-6)),
                        show.Genres.Intersect(showA.Genres).Except(showB.Genres).ToList(),
                        show.Genres.Intersect(showB.Genres).Except(showA.Genres).ToList(),
                        show.Genres.Intersect(showA.Genres).Intersect(showB.Genres).ToList());
                })
                .ToList(),
            Loose: ranked.Count == 0 || ranked[0].Score > model.MaxDistance));
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        return norm > 0 ? vector.Select(x => x / norm).ToArray() : vector;
    }
}
