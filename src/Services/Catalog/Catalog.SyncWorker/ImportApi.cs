using Catalog.SyncWorker.Providers;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Catalog.SyncWorker;

public record SearchImportRequest(string Query);

public record SearchImportResult(int Imported);

/// <param name="Started">False when a replay was already running.</param>
public record ReplayResult(bool Started);

public static class ImportApi
{
    /// <summary>
    /// How many of the provider's best matches a search may import.
    /// </summary>
    private const int SearchImportLimit = 5;

    public static RouteGroupBuilder MapImportApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/imports");

        api.MapPost("/search", ImportSearchResultsAsync);

        // Bring your history: the shows in someone's export, found by TheTVDB id or title and imported when missing.
        // Someone is watching a progress bar, so its calls go first.
        api.MapPost("/resolve", async (ResolveShowsRequest request, HistoryResolver resolver, IHostApplicationLifetime lifetime) =>
        {
            if (request.Shows is not { Count: > 0 and <= HistoryImports.MaxShows })
            {
                return Results.Problem($"Give 1 to {HistoryImports.MaxShows} shows.", statusCode: StatusCodes.Status400BadRequest);
            }
            using var caller = TvMazeCaller.Begin("history-import", TvMazeLane.Interactive);
            return Results.Ok(await resolver.ResolveAsync(request, lifetime.ApplicationStopping));
        });

        // The TVmaze budget over the last hour, by caller, for /ops; 404 with the mock provider, which calls nothing.
        api.MapGet("/tvmaze", (IServiceProvider services) =>
            services.GetService<TvMazeUsage>() is { } usage && services.GetService<TvMazeRateLimiter>() is { } limiter
                ? Results.Ok(usage.Report(limiter.Waiting))
                : Results.NotFound());

        // Used by a service that starts after shows were imported (Analytics on first start) to build its copy.
        api.MapPost("/replay", Replay);

        // The bulk import CLI (src/Tools/Catalog.ImportCli): start a job, follow it, cancel it.
        api.MapPost("/bulk", StartBulkImport);
        api.MapGet("/bulk/current", (BulkImportRunner runner) =>
            runner.Current is { } status ? Results.Ok(status) : Results.NotFound());
        api.MapGet("/bulk/{id:guid}", (Guid id, BulkImportRunner runner) =>
            runner.Current is { } status && status.Id == id ? Results.Ok(status) : Results.NotFound());
        api.MapDelete("/bulk/{id:guid}", (Guid id, BulkImportRunner runner) =>
            runner.TryCancel(id) ? Results.Accepted() : Results.NotFound());

        return api;
    }

    /// <summary>
    /// 202 with the new job, 409 with the running one if a job is already running, 400 for a bad request.
    /// </summary>
    private static IResult StartBulkImport(BulkImportRequest request, BulkImportRunner runner)
    {
        var titles = request.Titles?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ids = request.Ids?.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().ToList();

        var given = new[] { titles is { Count: > 0 }, ids is { Count: > 0 }, request.Top is not null }.Count(x => x);
        if (given != 1)
        {
            return Results.Problem("Give exactly one of titles, ids or top.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.Top is < 1 or > BulkImportRunner.MaxItems || titles?.Count > BulkImportRunner.MaxItems || ids?.Count > BulkImportRunner.MaxItems)
        {
            return Results.Problem($"A bulk import can include at most {BulkImportRunner.MaxItems} shows.", statusCode: StatusCodes.Status400BadRequest);
        }

        var (started, status) = runner.TryStart(new BulkImportRequest(titles is { Count: > 0 } ? titles : null, ids is { Count: > 0 } ? ids : null, request.Top));
        return started
            ? Results.Accepted($"/api/v1/imports/bulk/{status.Id}", status)
            : Results.Conflict(status);
    }

    /// <summary>
    /// Starts the replay in the background and answers straight away (202), so a client retry can't start another.
    /// </summary>
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

        // Not the request's token: if the caller stops waiting, finish anyway so the next search finds the shows. A search
        // is waiting on it, so its calls go first.
        using var caller = TvMazeCaller.Begin("search-import", TvMazeLane.Interactive);
        var imported = await synchronizer.ImportSearchResultsAsync(query, SearchImportLimit, lifetime.ApplicationStopping);
        return TypedResults.Ok(new SearchImportResult(imported));
    }
}
