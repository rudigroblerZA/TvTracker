namespace Catalog.SyncWorker;

/// <summary>
/// Runs a catalog replay in the background, one at a time. The replay endpoint answers straight away, so a caller's
/// timeout and retry can't start a second replay while the first is still publishing.
/// </summary>
public sealed class CatalogReplayRunner(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<CatalogReplayRunner> logger)
{
    private int _running;

    /// <summary>Starts a replay, or returns false when one is already running.</summary>
    public bool TryStart()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CatalogSynchronizer>().ReplayAllAsync(lifetime.ApplicationStopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Catalog replay failed");
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });

        return true;
    }
}
