namespace Catalog.SyncWorker;

/// <summary>
/// Every minute: publish EpisodeAired for episodes that just aired.
/// Every hour (and at startup): import featured shows, refresh shows that are due, resolve similar shows.
/// </summary>
public sealed class CatalogSyncService(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<CatalogSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastSync = null;
        using var timer = new PeriodicTimer(Tick);

        do
        {
            try
            {
                var now = time.GetUtcNow();
                if (lastSync is null || now - lastSync >= SyncInterval)
                {
                    await RunAsync(async s =>
                    {
                        var imported = await s.ImportFeaturedAsync(stoppingToken);
                        var refreshed = await s.RefreshDueAsync(stoppingToken);
                        var similar = await s.ResolveSimilarShowsAsync(stoppingToken);
                        logger.LogInformation("Catalog sync: {Imported} imported, {Refreshed} refreshed, {Similar} similar lists updated",
                            imported, refreshed, similar);
                    });
                    lastSync = now;
                }

                await RunAsync(async s =>
                {
                    var aired = await s.PublishAiredEpisodesAsync(stoppingToken);
                    if (aired > 0)
                    {
                        logger.LogInformation("Published EpisodeAired for {Count} episodes", aired);
                    }
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Catalog sync failed; will retry on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunAsync(Func<CatalogSynchronizer, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<CatalogSynchronizer>());
    }
}
