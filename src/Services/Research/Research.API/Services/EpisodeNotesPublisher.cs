using Microsoft.EntityFrameworkCore;
using Research.API.Apis;
using Research.API.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Research.API.Services;

/// <summary>
/// Sends every show's episode notes in one <see cref="EpisodeNotesChangedIntegrationEvent"/> shortly after start and
/// then daily, so Analytics' copy heals from a lost event (there's no dead-letter queue). Notes are written by hand,
/// a few hundred at most, so one event holds them all.
/// </summary>
public sealed class EpisodeNotesPublisher(IServiceScopeFactory scopes, TimeProvider time, ILogger<EpisodeNotesPublisher> logger)
    : BackgroundService
{
    /// <summary>Lets consumers bind their queues after a joint start.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstDelay, time, stoppingToken);
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ResearchContext>();
                var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();
                var takenAt = time.GetUtcNow();
                var notes = await db.EpisodeNotes.AsNoTracking().ToListAsync(stoppingToken);
                outbox.Add(new EpisodeNotesChangedIntegrationEvent(null, notes.Select(EpisodeNotesApi.ToItem).ToList(), takenAt));
                await db.SaveChangesAsync(stoppingToken);
                await outbox.PublishAddedAsync(stoppingToken);
                logger.LogInformation("Sent {Notes} episode notes", notes.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't send episode notes; trying again in a day");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
