using Microsoft.EntityFrameworkCore;
using Tracking.API.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Tracking.API.Services;

/// <summary>
/// Sends every user's follows and watched episodes as a <see cref="TrackingSnapshotIntegrationEvent"/> shortly after
/// start and then daily. Analytics and Recommendations repair their copies from it: a lost event (there's no
/// dead-letter queue) would otherwise leave a user's insights and picks wrong for good.
/// </summary>
public sealed class ActivitySnapshotPublisher(IServiceScopeFactory scopes, TimeProvider time, ILogger<ActivitySnapshotPublisher> logger)
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
                logger.LogWarning(ex, "Couldn't send activity snapshots; trying again in a day");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        List<string> users;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackingContext>();
            users = await db.Follows.Select(f => f.UserId).Union(db.WatchedEpisodes.Select(w => w.UserId)).ToListAsync(ct);
        }

        foreach (var batch in users.Chunk(UsersPerBatch))
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackingContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();

            var takenAt = time.GetUtcNow();
            var follows = (await db.Follows.AsNoTracking().Where(f => batch.Contains(f.UserId)).ToListAsync(ct)).ToLookup(f => f.UserId);
            var watches = (await db.WatchedEpisodes.AsNoTracking().Where(w => batch.Contains(w.UserId)).ToListAsync(ct)).ToLookup(w => w.UserId);
            foreach (var user in batch)
            {
                outbox.Add(new TrackingSnapshotIntegrationEvent(
                    user,
                    takenAt,
                    follows[user].Select(f => new SnapshotFollow(f.ShowId, new DateTimeOffset(f.FollowedAtUtc, TimeSpan.Zero))).ToList(),
                    watches[user].Select(w => new SnapshotWatch(w.ShowId, w.EpisodeId, new DateTimeOffset(w.WatchedAtUtc, TimeSpan.Zero))).ToList()));
            }

            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
        }

        logger.LogInformation("Sent activity snapshots for {Users} users", users.Count);
    }
}
