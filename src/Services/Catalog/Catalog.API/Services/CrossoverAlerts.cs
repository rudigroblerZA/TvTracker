using TvTracker;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Services;

/// <summary>
/// Sends a <see cref="CrossoverScheduledIntegrationEvent"/> every hour for each crossover starting within
/// <see cref="LeadTime"/>. Catalog.API is the catalog's read side and keeps no outbox, so the events go straight to the
/// bus and are sent again each hour while the crossover is ahead: a lost one heals, and Notifications keys on the
/// crossover's id, so followers hear once.
/// </summary>
public sealed class CrossoverAlerts(Crossovers crossovers, IEventBus bus, TimeProvider time, ILogger<CrossoverAlerts> logger)
    : BackgroundService
{
    /// <summary>How far ahead followers hear of a crossover.</summary>
    public static readonly TimeSpan LeadTime = TimeSpan.FromDays(14);

    /// <summary>Lets Notifications bind its queue after a joint start.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstDelay, time, stoppingToken);
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                var now = time.GetUtcNow();
                var soon = (await crossovers.UpcomingAsync(stoppingToken))
                    .Where(c => c.StartsAtUtc > now && c.StartsAtUtc <= now + LeadTime)
                    .ToList();
                foreach (var c in soon)
                {
                    await bus.PublishAsync(new CrossoverScheduledIntegrationEvent(c.Id, c.Name, c.UniverseId, c.UniverseName,
                        c.Parts.Select(p => new CrossoverPartItem(p.ShowId, p.ShowTitle, p.EpisodeId, p.SeasonNumber, p.EpisodeNumber, p.AirsAtUtc)).ToList()),
                        stoppingToken);
                }
                logger.LogInformation("Crossover alerts: {Count} crossovers in the next {Days} days", soon.Count, LeadTime.TotalDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't send crossover alerts; trying again in an hour");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
