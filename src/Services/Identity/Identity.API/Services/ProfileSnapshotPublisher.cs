using Identity.API.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Identity.API.Services;

/// <summary>
/// Sends every user's display name and plan as a <see cref="UserProfileSnapshotIntegrationEvent"/> shortly after start
/// and then daily. Social, which started after most users registered, learns their names from it; Tracking their plans;
/// a lost profile or plan event heals.
/// </summary>
public sealed class ProfileSnapshotPublisher(IServiceScopeFactory scopes, TimeProvider time, ILogger<ProfileSnapshotPublisher> logger)
    : BackgroundService
{
    /// <summary>Lets consumers bind their queues after a joint start.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private const int ProfilesPerBatch = 200;

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
                logger.LogWarning(ex, "Couldn't send profile snapshots; trying again in a day");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        var sent = 0;
        string? after = null;
        while (true)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();

            var batch = await db.Profiles.AsNoTracking()
                .Where(p => after == null || string.Compare(p.UserId, after) > 0)
                .OrderBy(p => p.UserId)
                .Take(ProfilesPerBatch)
                .Select(p => new { p.UserId, p.DisplayName, p.Plan, p.PlanChangedAtUtc })
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var profile in batch)
            {
                outbox.Add(new UserProfileSnapshotIntegrationEvent(profile.UserId, profile.DisplayName, profile.Plan,
                    profile.PlanChangedAtUtc is { } changed ? new DateTimeOffset(changed, TimeSpan.Zero) : null));
            }
            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
            sent += batch.Count;
            after = batch[^1].UserId;
        }

        logger.LogInformation("Sent profile snapshots for {Profiles} users", sent);
    }
}
