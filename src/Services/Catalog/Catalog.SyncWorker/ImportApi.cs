using Microsoft.AspNetCore.Http.HttpResults;

namespace Catalog.SyncWorker;

public record SearchImportRequest(string Query);

public record SearchImportResult(int Imported);

public static class ImportApi
{
    /// <summary>How many of the provider's best matches a search may import.</summary>
    private const int SearchImportLimit = 5;

    public static RouteGroupBuilder MapImportApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/imports");

        api.MapPost("/search", ImportSearchResultsAsync);

        return api;
    }

    private static async Task<Results<Ok<SearchImportResult>, BadRequest>> ImportSearchResultsAsync(
        SearchImportRequest request, CatalogSynchronizer synchronizer, IHostApplicationLifetime lifetime)
    {
        var query = request.Query?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < 2)
        {
            return TypedResults.BadRequest();
        }

        // Not the request's token: if the caller stops waiting, finish anyway so the next search finds the shows.
        var imported = await synchronizer.ImportSearchResultsAsync(query, SearchImportLimit, lifetime.ApplicationStopping);
        return TypedResults.Ok(new SearchImportResult(imported));
    }
}
