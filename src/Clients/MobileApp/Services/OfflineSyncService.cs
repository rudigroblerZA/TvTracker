using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TvTracker.UI.Services;

namespace TvTracker.Mobile.Services;

/// <summary>
/// Replays changes queued while offline, oldest first, when the connection comes back
/// (and at start-up). Watched marks carry their original time, so the most recent change wins on the server.
/// </summary>
public sealed class OfflineSyncService(
    SqliteOfflineStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<OfflineSyncService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _retryTimer;

    public void Start()
    {
        Connectivity.Current.ConnectivityChanged += (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                _ = SyncAsync();
            }
        };

        // While changes are waiting, retry every 30 seconds in case the server was down rather than the network.
        store.RequestQueued += () => _retryTimer ??= new Timer(_ => _ = SyncAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        _ = SyncAsync();
    }

    public async Task SyncAsync()
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet || !await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var queue = await store.GetQueueAsync();
            if (queue.Count == 0)
            {
                StopRetrying();
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            // Any API client can replay a request; it targets the queued request's own named client.
            var client = scope.ServiceProvider.GetRequiredService<TrackingClient>();

            foreach (var item in queue)
            {
                var pending = new PendingRequest(item.Client, item.Method, item.Url, item.Body, item.CreatedAtUtc);
                if (!await client.ReplayAsync(pending, CancellationToken.None))
                {
                    // Server error: keep the rest in order and try again later.
                    return;
                }
                await store.RemoveAsync(item);
            }

            logger.LogInformation("Replayed {Count} offline changes", queue.Count);
            StopRetrying();
        }
        catch (HttpRequestException)
        {
            // Still offline; the next connectivity change or retry tick will try again.
        }
        catch (SignInRequiredException)
        {
            logger.LogInformation("Offline changes are waiting for the user to sign in again");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopRetrying()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
    }
}
