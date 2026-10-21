namespace Analytics.Worker;

/// <summary>Recomputes all insights shortly after startup, then every <c>Analytics:RefreshInterval</c> (default one hour).</summary>
public sealed class InsightsRefresher(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<InsightsRefresher> logger) : BackgroundService
{
    /// <summary>Gives the event consumers (and a catalog replay) a head start before the first run.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>How often every show's tags are sent, not just the changed ones (and on the first run after starting).</summary>
    private static readonly TimeSpan FullTagSyncInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("Analytics:RefreshInterval", TimeSpan.FromHours(1));
        await Task.Delay(configuration.GetValue("Analytics:StartupDelay", StartupDelay), stoppingToken);

        using var timer = new PeriodicTimer(interval);
        var lastFullTagSync = DateTime.MinValue;
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var allTags = DateTime.UtcNow - lastFullTagSync >= FullTagSyncInterval;
                await scope.ServiceProvider.GetRequiredService<InsightsComputer>().ComputeAllAsync(stoppingToken, allTags);
                if (allTags)
                {
                    lastFullTagSync = DateTime.UtcNow;
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
