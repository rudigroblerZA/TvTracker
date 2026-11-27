using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TvTracker.IntegrationEvents;

namespace Catalog.Mixes;

/// <param name="Inputs">The mix's shows, in the order asked.</param>
/// <param name="Indexed">False while any of them still waits to be embedded; <paramref name="Matches"/> is empty then.</param>
public sealed record MixOutcome(IReadOnlyList<Show> Inputs, bool Indexed, IReadOnlyList<MixMatch> Matches, bool Loose);

public sealed record MixMatch(Show Show, IReadOnlyList<double> Distances, double Score);

/// <summary>
/// Runs a mix against the catalog: loads the shows and their meaning vectors (the ones semantic search uses), gathers
/// candidates, and ranks them by <see cref="MixRules"/>. Blend and Mixtape both answer from this.
/// <list type="bullet">
/// <item><b>Candidates:</b> the shows nearest the mix, plus each show's own nearest, in one query (a UNION ALL of
/// nearest-neighbour scans). pgvector's HNSW scan returns at most <c>ef_search</c> (40) rows a query, and the best
/// common ground isn't always among the 40 nearest the mix.</item>
/// <item><b>Excluded:</b> the mix's shows, and shows titled like any of them (a remake, the classic Doctor Who).</item>
/// <item><b>Same kinds only:</b> <see cref="MixRules.FitsKinds"/>. Ted Lasso + Succession + Breaking Bad brought in
/// The Amazing Race, close enough in summary words. Mixing in an animated show lets animation in.</item>
/// </list>
/// </summary>
public static class ShowMixer
{
    private const int CandidatesPerQuery = 40;

    /// <returns>Null when one of the shows isn't in the catalog.</returns>
    public static async Task<MixOutcome?> MixAsync(CatalogContext db, EmbeddingModel model, IReadOnlyList<Guid> showIds,
        IReadOnlyList<double> weights, int maxMatches, CancellationToken ct)
    {
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        if (shows.Count != showIds.Count)
        {
            return null;
        }
        var inputs = showIds.Select(id => shows[id]).ToList();

        var stored = await db.ShowEmbeddings.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.Model == model.Id)
            .ToDictionaryAsync(e => e.ShowId, e => e.Embedding.ToArray(), ct);
        if (stored.Count != showIds.Count)
        {
            // Just imported (e.g. found by search a moment ago): the sync worker embeds it within a minute.
            return new MixOutcome(inputs, false, [], false);
        }
        var vectors = showIds.Select(id => stored[id]).ToList();

        var titles = inputs.Select(s => s.Title).ToList();
        var eligible = db.ShowEmbeddings.AsNoTracking()
            .Where(e => e.Model == model.Id && !showIds.Contains(e.ShowId))
            .Where(e => db.Shows.Any(s => s.Id == e.ShowId && !titles.Contains(s.Title)));

        var nearest = vectors.Prepend(MixRules.Mix(vectors, weights))
            .Select(query => new Vector(query))
            .Select(vector => eligible.OrderBy(e => e.Embedding.CosineDistance(vector)).Take(CandidatesPerQuery).Select(e => e.ShowId))
            .Aggregate((all, next) => all.Concat(next));
        var candidateIds = (await nearest.ToListAsync(ct)).ToHashSet();

        // The candidates' vectors and kinds in one go; those of another kind than the mix's are left out.
        var kinds = inputs.Select(s => ShowKinds.Of(s.Keywords)).OfType<string>().ToHashSet();
        var candidates = await eligible.Where(e => candidateIds.Contains(e.ShowId))
            .Join(db.Shows, e => e.ShowId, s => s.Id, (e, s) => new { e.ShowId, e.Embedding, s.Keywords })
            .ToListAsync(ct);
        var ranked = MixRules.Rank(
            candidates.Where(c => MixRules.FitsKinds(kinds, ShowKinds.Of(c.Keywords))).Select(c => (c.ShowId, c.Embedding.ToArray())),
            vectors, weights, maxMatches);

        var matchIds = ranked.Select(r => r.ShowId).ToList();
        var matches = await db.Shows.AsNoTracking().Where(s => matchIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        return new MixOutcome(
            inputs,
            true,
            ranked.Where(r => matches.ContainsKey(r.ShowId)).Select(r => new MixMatch(matches[r.ShowId], r.Distances, r.Score)).ToList(),
            MixRules.Loose(ranked, model.MaxDistance));
    }
}
