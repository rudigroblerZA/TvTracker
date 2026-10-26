using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Ratings.API.Services;

/// <summary>
/// Sends every user's show and episode ratings as a <see cref="RatingsSnapshotIntegrationEvent"/> shortly after start
/// and then daily, so Analytics and Recommendations can repair copies that missed events (as Tracking does for
/// activity).
/// </summary>
public sealed class RatingsSnapshotPublisher(IServiceScopeFactory scopes, TimeProvider time, ILogger<RatingsSnapshotPublisher> logger)
    : BackgroundService
{
    /// <summary>Lets consumers bind their queues after a joint start.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private const int UsersPerBatch = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstDelay, time, stoppingToken);
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await PublishAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't send ratings snapshots; trying again in a day");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        List<string> users;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RatingsContext>();
            users = await db.ShowRatings.Select(r => r.UserId).Union(db.EpisodeRatings.Select(r => r.UserId)).ToListAsync(ct);
        }

        foreach (var batch in users.Chunk(UsersPerBatch))
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RatingsContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();

            var takenAt = time.GetUtcNow();
            var shows = (await db.ShowRatings.AsNoTracking().Where(r => batch.Contains(r.UserId)).ToListAsync(ct)).ToLookup(r => r.UserId);
            var episodes = (await db.EpisodeRatings.AsNoTracking().Where(r => batch.Contains(r.UserId)).ToListAsync(ct)).ToLookup(r => r.UserId);
            foreach (var user in batch)
            {
                outbox.Add(new RatingsSnapshotIntegrationEvent(
                    user,
                    takenAt,
                    shows[user].Select(r => new SnapshotShowRating(r.ShowId, r.Stars, new DateTimeOffset(r.RatedAtUtc, TimeSpan.Zero))).ToList(),
                    episodes[user].Select(r => new SnapshotEpisodeRating(r.ShowId, r.EpisodeId, r.Stars, new DateTimeOffset(r.RatedAtUtc, TimeSpan.Zero))).ToList()));
            }

            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
        }

        logger.LogInformation("Sent ratings snapshots for {Users} users", users.Count);
    }
}
