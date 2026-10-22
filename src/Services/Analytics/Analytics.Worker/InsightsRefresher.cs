using Analytics.Worker.Insights;

namespace Analytics.Worker;

/// <summary>Recomputes all insights shortly after startup, then every <c>Analytics:RefreshInterval</c> (default one hour).</summary>
public sealed class InsightsRefresher(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<InsightsRefresher> logger) : BackgroundService
{
    /// <summary>Gives the event consumers (and a catalog replay) a head start before the first run.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>How often every show's tags and DNA are sent, not just the changed ones (and on the first run after starting).</summary>
    private static readonly TimeSpan FullSyncInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("Analytics:RefreshInterval", TimeSpan.FromHours(1));
        await Task.Delay(configuration.GetValue("Analytics:StartupDelay", StartupDelay), stoppingToken);

        using var timer = new PeriodicTimer(interval);
        var lastFullSync = DateTime.MinValue;
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var publishAll = DateTime.UtcNow - lastFullSync >= FullSyncInterval;
                await scope.ServiceProvider.GetRequiredService<InsightsPipeline>().RunAsync(stoppingToken, publishAll);
                if (publishAll)
                {
                    lastFullSync = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Computing insights failed; will retry at the next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
