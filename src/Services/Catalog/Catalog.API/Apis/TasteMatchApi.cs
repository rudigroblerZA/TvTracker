using Catalog.Data;
using Catalog.Embeddings;
using Catalog.Mixes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Hosting;

namespace Catalog.API.Apis;

public record TasteSideRequest(IReadOnlyList<WeightedShowDto> Loved, IReadOnlyList<Guid> Known);

public record WeightedShowDto(Guid ShowId, double Weight);

/// <param name="SeedsPerSide">How many of each side's heaviest shows seed the suggestions.</param>
public record TasteCompareRequest(TasteSideRequest First, TasteSideRequest Second, int SeedsPerSide, int MaxTogether);

/// <summary>
/// Taste Match's catalog half: how alike two sets of shows are in meaning, and what sits between them
/// (<see cref="TasteMatcher"/>). For other services only (Analytics, which owns the matches): it is handed show ids
/// and weights, never whose they are, and stores nothing.
/// </summary>
public static class TasteMatchApi
{
    /// <summary>
    /// The most shows a side may send; more is a caller's mistake.
    /// </summary>
    private const int MaxLoved = 100;

    private const int MaxKnown = 5000;

    private static readonly TimeSpan ReferenceFor = TimeSpan.FromHours(6);

    public static void MapTasteMatchApi(this RouteGroupBuilder api) =>
        api.MapPost("/shows/taste-match", CompareAsync).RequireAuthorization(AuthenticationExtensions.InternalPolicy);

    private static async Task<Results<Ok<TasteComparison>, ValidationProblem>> CompareAsync(
        TasteCompareRequest request, CatalogContext db, EmbeddingModel model, HybridCache cache, CancellationToken ct)
    {
        if (new[] { request.First, request.Second }.Any(s => s.Loved.Count > MaxLoved || s.Known.Count > MaxKnown)
            || request.SeedsPerSide is < 1 or > 5 || request.MaxTogether is < 0 or > 40)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Too many shows, seeds or suggestions asked for."] });
        }

        static TasteSide Side(TasteSideRequest side) => new(
            side.Loved.Where(l => l.Weight > 0).GroupBy(l => l.ShowId).ToDictionary(g => g.Key, g => g.Max(l => l.Weight)),
            side.Known.ToHashSet());

        // How far apart unrelated shows are moves only with the catalog and the model: measured once in a while.
        var reference = await cache.GetOrCreateAsync($"catalog:taste-reference:{model.Id}",
            async token => await TasteMatcher.ReferenceDistanceAsync(db, model, token),
            new HybridCacheEntryOptions { Expiration = ReferenceFor, LocalCacheExpiration = ReferenceFor }, cancellationToken: ct);

        return TypedResults.Ok(await TasteMatcher.CompareAsync(db, model, reference, Side(request.First), Side(request.Second),
            request.SeedsPerSide, request.MaxTogether, ct));
    }
}
