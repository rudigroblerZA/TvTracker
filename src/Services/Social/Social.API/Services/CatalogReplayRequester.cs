using Microsoft.EntityFrameworkCore;
using Social.API.Data;

namespace Social.API.Services;

/// <summary>
/// Backfill: when Social has no shows yet (its first start), or episodes from before it kept air times, asks the catalog
/// sync worker to republish the catalog, so the feed can name shows and progress can count aired episodes. Analytics
/// does the same on its first start.
/// </summary>
public sealed class CatalogReplayRequester(IServiceScopeFactory scopeFactory, HttpClient http, ILogger<CatalogReplayRequester> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private const int MaxAttempts = 40;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SocialContext>();
            if (await db.Shows.AnyAsync(stoppingToken) && await db.Episodes.AnyAsync(e => e.AirsAtUtc != null, stoppingToken))
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
                logger.LogInformation("Asked the catalog sync worker to replay the catalog for Social's copy");
                return;
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(ex, "Catalog sync worker not reachable yet (attempt {Attempt})", attempt);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }

        logger.LogWarning("Couldn't reach the catalog sync worker to replay the catalog; Social fills only from new catalog events");
    }
}
