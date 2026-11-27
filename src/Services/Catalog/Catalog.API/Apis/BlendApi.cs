using Catalog.API.Services;
using Catalog.Data;
using Catalog.Embeddings;
using Catalog.Mixes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
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
/// Show blend: two shows in, the shows that sit between them out ("Breaking Bad × The Office"). A mix of two with a
/// lean (<see cref="ShowMixer"/>, which Mixtape answers from too): a show only like one of them (an NCIS spinoff,
/// blended with Friends) ranks low, however close it is to that one.
/// </summary>
public static class BlendApi
{
    public const int MaxMatches = 20;

    public static void MapBlendApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/blend", GetBlendAsync).RequireRateLimiting(SearchRateLimiting.Policy).RequireFeature(FeatureFlags.Blend);

    /// <param name="lean">0-100, how much like <paramref name="a"/> (default 50); 20-80 keeps both shows in the mix.</param>
    private static async Task<Results<Ok<BlendDto>, NotFound, ValidationProblem>> GetBlendAsync(
        Guid a, Guid b, CatalogContext db, EmbeddingModel model, int lean = 50, CancellationToken ct = default)
    {
        if (a == b)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["b"] = ["Pick two different shows."] });
        }
        lean = Math.Clamp(lean, 0, 100);

        if (await ShowMixer.MixAsync(db, model, [a, b], MixRules.LeanWeights(lean), MaxMatches, ct) is not { } mix)
        {
            return TypedResults.NotFound();
        }

        var (showA, showB) = (mix.Inputs[0], mix.Inputs[1]);
        return TypedResults.Ok(new BlendDto(
            showA.ToSummary(),
            showB.ToSummary(),
            lean,
            mix.Indexed,
            mix.Matches
                .Select(m => new BlendMatchDto(
                    m.Show.ToSummary(),
                    MixRules.Percent(m.Score),
                    MixRules.Share(m.Distances[0], m.Distances[1]),
                    m.Show.Genres.Intersect(showA.Genres).Except(showB.Genres).ToList(),
                    m.Show.Genres.Intersect(showB.Genres).Except(showA.Genres).ToList(),
                    m.Show.Genres.Intersect(showA.Genres).Intersect(showB.Genres).ToList()))
                .ToList(),
            mix.Loose));
    }
}
