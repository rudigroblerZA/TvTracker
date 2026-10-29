using Catalog.API.Services;
using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="Shows">The mixtape's shows, in the order asked.</param>
/// <param name="Indexed">False while any of them still waits to be embedded; <paramref name="Matches"/> is empty then.</param>
/// <param name="SharedGenres">Genres at least two of the shows have: what the mixtape is "about".</param>
/// <param name="Loose">
/// The shows have little in common: even the best match is farther from one of them than search's cutoff for
/// "similar" (the model's <see cref="EmbeddingModel.MaxDistance"/>). The matches are the closest common ground, not close.
/// </param>
public record MixtapeDto(IReadOnlyList<ShowSummaryDto> Shows, bool Indexed, IReadOnlyList<string> SharedGenres,
    IReadOnlyList<MixtapeMatchDto> Matches, bool Loose = false);

/// <param name="Fit">How well it fits all of them, 0-100, from its distance to the farthest.</param>
/// <param name="Links">How it relates to each of the mixtape's shows, in the same order as <see cref="MixtapeDto.Shows"/>.</param>
public record MixtapeMatchDto(ShowSummaryDto Show, int Fit, IReadOnlyList<MixtapeLinkDto> Links);

/// <param name="Closeness">0-100, from its distance to that show (100 = the same meaning).</param>
/// <param name="SharedGenres">Genres it shares with that show.</param>
public record MixtapeLinkDto(Guid ShowId, int Closeness, IReadOnlyList<string> SharedGenres);

/// <summary>
/// Mixtape: three to five shows in, the shows that fit all of them out. Blend for more than two: the shows' meaning
/// vectors (the ones semantic search uses) are averaged, and candidates are ranked by their distance to the farthest
/// of the mixtape's shows, so a show close to just one of them ranks low however close it is.
/// <list type="bullet">
/// <item><b>Candidates:</b> the shows nearest the mix, plus each show's own nearest, in one query (a UNION ALL of
/// nearest-neighbour scans). pgvector's HNSW scan returns at most <c>ef_search</c> (40) rows a query, and with three or
/// more shows the best common ground isn't always among the 40 nearest the average.</item>
/// <item><b>Excluded:</b> the mixtape's shows, and shows titled like any of them (a remake, the classic Doctor Who).</item>
/// <item><b>Same kinds only:</b> a pick's kind (scripted, animation, reality, …, from the catalog's keywords) must be one of
/// the mixtape's. Ted Lasso + Succession + Breaking Bad brought in The Amazing Race, close enough in summary words.
/// Mixing in an animated show lets animation in.</item>
/// </list>
/// </summary>
public static class MixtapeApi
{
    public const int MinShows = 3;
    public const int MaxShows = 5;
    public const int MaxMatches = 24;

    private const int CandidatesPerQuery = 40;

    public static void MapMixtapeApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/mixtape", GetMixtapeAsync).RequireRateLimiting(SearchRateLimiting.Policy).RequireFeature(FeatureFlags.Mixtape);

    /// <param name="ids">The shows, comma-separated: <see cref="MinShows"/> to <see cref="MaxShows"/> different ones.</param>
    private static async Task<Results<Ok<MixtapeDto>, NotFound, ValidationProblem>> GetMixtapeAsync(
        string? ids, CatalogContext db, EmbeddingModel model, CancellationToken ct = default)
    {
        var showIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .ToList();
        if (showIds.Contains(Guid.Empty) || showIds.Distinct().Count() != showIds.Count || showIds.Count is < MinShows or > MaxShows)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ids"] = [$"Pick {MinShows} to {MaxShows} different shows."]
            });
        }

        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        if (shows.Count != showIds.Count)
        {
            return TypedResults.NotFound();
        }
        var inputs = showIds.Select(id => shows[id]).ToList();
        var summaries = inputs.Select(s => s.ToSummary()).ToList();
        var sharedGenres = inputs.SelectMany(s => s.Genres.Distinct())
            .GroupBy(g => g)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key)
            .ToList();

        var vectors = await db.ShowEmbeddings.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.Model == model.Id)
            .ToDictionaryAsync(e => e.ShowId, e => e.Embedding.ToArray(), ct);
        if (vectors.Count != showIds.Count)
        {
            // Just imported (e.g. found by search a moment ago): the sync worker embeds it within a minute.
            return TypedResults.Ok(new MixtapeDto(summaries, false, sharedGenres, []));
        }

        var kinds = inputs.Select(s => ShowKinds.Of(s.Keywords)).OfType<string>().ToHashSet();
        var inputVectors = showIds.Select(id => vectors[id]).ToList();
        var mix = Normalize(Enumerable.Range(0, inputVectors[0].Length)
            .Select(i => inputVectors.Sum(v => v[i]) / inputVectors.Count)
            .ToArray());

        // Same-titled shows aren't part of the mix.
        var titles = inputs.Select(s => s.Title).ToList();
        var eligible = db.ShowEmbeddings.AsNoTracking()
            .Where(e => e.Model == model.Id && !showIds.Contains(e.ShowId))
            .Where(e => db.Shows.Any(s => s.Id == e.ShowId && !titles.Contains(s.Title)));

        var nearest = inputVectors.Prepend(mix)
            .Select(query => new Vector(query))
            .Select(vector => eligible.OrderBy(e => e.Embedding.CosineDistance(vector)).Take(CandidatesPerQuery).Select(e => e.ShowId))
            .Aggregate((all, next) => all.Concat(next));
        var candidateIds = (await nearest.ToListAsync(ct)).ToHashSet();

        // A show of another kind than the mixtape's is left out; one with no kind tag stays.
        var candidateKinds = await db.Shows.AsNoTracking().Where(s => candidateIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Keywords })
            .ToDictionaryAsync(s => s.Id, s => ShowKinds.Of(s.Keywords), ct);
        candidateIds.RemoveWhere(id => kinds.Count > 0 && candidateKinds.GetValueOrDefault(id) is { } kind && !kinds.Contains(kind));

        // Vectors are L2-normalised, so cosine distance is 1 − the dot product.
        var candidates = await eligible.Where(e => candidateIds.Contains(e.ShowId))
            .Select(e => new { e.ShowId, e.Embedding })
            .ToListAsync(ct);
        var ranked = candidates
            .Select(c =>
            {
                var v = c.Embedding.ToArray();
                var distances = inputVectors.Select(input => 1 - Dot(v, input)).ToList();
                return (c.ShowId, Distances: distances, Score: distances.Max());
            })
            .OrderBy(c => c.Score)
            .Take(MaxMatches)
            .ToList();

        var matchIds = ranked.Select(r => r.ShowId).ToList();
        var matches = await db.Shows.AsNoTracking().Where(s => matchIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        return TypedResults.Ok(new MixtapeDto(
            summaries,
            true,
            sharedGenres,
            ranked
                .Where(r => matches.ContainsKey(r.ShowId))
                .Select(r =>
                {
                    var show = matches[r.ShowId];
                    return new MixtapeMatchDto(
                        show.ToSummary(),
                        Percent(r.Score),
                        inputs.Select((input, i) => new MixtapeLinkDto(input.Id, Percent(r.Distances[i]),
                            show.Genres.Intersect(input.Genres).ToList())).ToList());
                })
                .ToList(),
            Loose: ranked.Count == 0 || ranked[0].Score > model.MaxDistance));
    }

    private static int Percent(double distance) => (int)Math.Round(100 * Math.Clamp(1 - distance, 0, 1));

    private static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        return norm > 0 ? vector.Select(x => x / norm).ToArray() : vector;
    }
}
