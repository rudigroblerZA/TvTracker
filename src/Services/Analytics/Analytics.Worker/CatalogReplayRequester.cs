using Analytics.Data;
using Microsoft.EntityFrameworkCore;
using Polly;

namespace Analytics.Worker;

/// <summary>
/// Backfill: when Analytics has no shows yet (first deployment, or a new database), or holds cast credits from before
/// catalog events carried person ids, or shows from before they carried creatives or their network, asks the catalog sync worker to
/// republish every show, its episodes, and people's careers.
/// Shows imported before then reach it like new ones. Our users' earlier ratings aren't replayed; they accumulate.
/// </summary>
public sealed class CatalogReplayRequester(
    IServiceScopeFactory scopeFactory, HttpClient http, ILogger<CatalogReplayRequester> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private const int MaxAttempts = 40;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnalyticsContext>();
            var hasShows = await db.Shows.AnyAsync(stoppingToken);
            var castWithoutPersonIds = await db.Cast.AnyAsync(c => c.PersonId == null, stoppingToken);
            var showsWithoutCreatives = await db.Shows.AnyAsync(s => !s.CreativesKnown || !s.NetworkKnown, stoppingToken);
            if (hasShows && !castWithoutPersonIds && !showsWithoutCreatives)
            {
                return;
            }
        }

        // The sync worker starts after this service is healthy (so no catalog events are missed); keep trying until it's up.
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var response = await http.PostAsync("api/v1/imports/replay", null, stoppingToken);
                response.EnsureSuccessStatusCode();
                // Accepted: the replay is under way whatever the body says, so it's never asked for again.
                logger.LogInformation(await AlreadyRunningAsync(response, stoppingToken)
                    ? "Catalog replay already running"
                    : "Catalog replay started; shows and episodes arrive as catalog events");
                return;
            }
            // Not reachable yet: refused or failed, timed out (the client's own timeout, or the resilience handler's),
            // or the circuit is open. Anything else is a fault of ours and is left to surface.
            catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException
                                       || (ex is OperationCanceledException && !stoppingToken.IsCancellationRequested))
            {
                logger.LogDebug(ex, "Catalog sync worker not reachable yet (attempt {Attempt})", attempt);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }

        logger.LogWarning("Couldn't reach the catalog sync worker to replay the catalog; Analytics fills only from new catalog events");
    }

    /// <summary>
    /// What the answer says of whether a replay was already running; false when it doesn't say.
    /// </summary>
    private static async Task<bool> AlreadyRunningAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<ReplayResult>(ct))?.Started == false;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or HttpRequestException)
        {
            return false;
        }
    }

    private sealed record ReplayResult(bool Started);
}
