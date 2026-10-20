using Microsoft.AspNetCore.Http.HttpResults;

namespace Catalog.SyncWorker;

public record SearchImportRequest(string Query);

public record SearchImportResult(int Imported);

/// <param name="Started">False when a replay was already running.</param>
public record ReplayResult(bool Started);

public static class ImportApi
{
    /// <summary>How many of the provider's best matches a search may import.</summary>
    private const int SearchImportLimit = 5;

    public static RouteGroupBuilder MapImportApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/imports");

        api.MapPost("/search", ImportSearchResultsAsync);

        // Used by a service that starts after shows were imported (Analytics on first start) to build its copy.
        api.MapPost("/replay", Replay);

        return api;
    }

    /// <summary>Starts the replay in the background and answers straight away (202), so a client retry can't start another.</summary>
    private static Accepted<ReplayResult> Replay(CatalogReplayRunner runner) =>
        TypedResults.Accepted((string?)null, new ReplayResult(runner.TryStart()));

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
