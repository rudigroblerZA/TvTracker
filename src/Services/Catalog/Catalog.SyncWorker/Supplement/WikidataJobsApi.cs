using TvTracker;
namespace Catalog.SyncWorker.Supplement;

/// <summary>
/// The sync worker's jobs that add Wikidata's data to the catalog, by the names the CLI gives them.
/// </summary>
public static class WikidataJobs
{
    /// <summary>
    /// The TV atlas: where shows are set and filmed (<c>SettingsService</c>).
    /// </summary>
    public const string Atlas = "atlas";

    /// <summary>
    /// Gap-fill: overview, network, premiere date and poster for shows without (<see cref="WikidataSupplementService"/>).
    /// </summary>
    public const string Fill = "fill";

    public static readonly IReadOnlyList<string> All = [Atlas, Fill];
}

public static class WikidataJobsApi
{
    /// <summary>
    /// For the import CLI (<c>tvtracker-import wikidata</c>): how each job is doing, and running one now.
    /// </summary>
    public static RouteGroupBuilder MapWikidataJobsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/wikidata/jobs").RequireAuthorization(AuthenticationExtensions.InternalPolicy);

        api.MapGet("/", (IServiceProvider services) =>
            Results.Ok(WikidataJobs.All.Select(name => services.GetRequiredKeyedService<JobRun>(name).Status).ToList()));

        // 202 with the job's status; 404 for a name that isn't a job; 409 with the status when the job is off.
        api.MapPost("/{name}/run", (string name, IServiceProvider services) =>
        {
            if (!WikidataJobs.All.Contains(name))
            {
                return Results.NotFound();
            }
            var run = services.GetRequiredKeyedService<JobRun>(name);
            return run.Request() ? Results.Accepted(value: run.Status) : Results.Conflict(run.Status);
        });

        return api;
    }
}
