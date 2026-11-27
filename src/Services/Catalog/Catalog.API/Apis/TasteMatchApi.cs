using Catalog.Data;
using Catalog.Embeddings;
using Catalog.Mixes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Hosting;

namespace Catalog.API.Apis;

public record WeightedShowDto(Guid ShowId, double Weight);

/// <param name="First">One person's shows to compare, each with how much it counts; at most <see cref="TasteRules.MaxLoved"/>.</param>
public record TasteCompareRequest(IReadOnlyList<WeightedShowDto> First, IReadOnlyList<WeightedShowDto> Second);

/// <summary>
/// Taste Match's catalog half: how alike two sets of shows are in meaning, and what sits between them
/// (<see cref="TasteMatcher"/>). For other services only (Analytics, which owns the matches): it is handed show ids
/// and weights, never whose they are, and stores nothing.
/// </summary>
public static class TasteMatchApi
{
    private static readonly TimeSpan ReferenceFor = TimeSpan.FromHours(6);

    public static void MapTasteMatchApi(this RouteGroupBuilder api) =>
        api.MapPost("/shows/taste-match", CompareAsync).RequireAuthorization(AuthenticationExtensions.InternalPolicy);

    private static async Task<Results<Ok<TasteComparison>, ValidationProblem>> CompareAsync(
        TasteCompareRequest request, CatalogContext db, EmbeddingModel model, HybridCache cache, CancellationToken ct)
    {
        if (request.First.Count > TasteRules.MaxLoved || request.Second.Count > TasteRules.MaxLoved)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [$"A side has at most {TasteRules.MaxLoved} shows."] });
        }

        static Dictionary<Guid, double> Side(IReadOnlyList<WeightedShowDto> side) =>
            side.Where(l => l.Weight > 0).GroupBy(l => l.ShowId).ToDictionary(g => g.Key, g => g.Max(l => l.Weight));

        // How far apart unrelated shows are moves only with the catalog and the model: measured once in a while.
        var reference = await cache.GetOrCreateAsync($"catalog:taste-reference:{model.Id}",
            async token => await TasteMatcher.ReferenceDistanceAsync(db, model, token),
            new HybridCacheEntryOptions { Expiration = ReferenceFor, LocalCacheExpiration = ReferenceFor }, cancellationToken: ct);

        return TypedResults.Ok(await TasteMatcher.CompareAsync(db, model, reference, Side(request.First), Side(request.Second), ct));
    }
}
