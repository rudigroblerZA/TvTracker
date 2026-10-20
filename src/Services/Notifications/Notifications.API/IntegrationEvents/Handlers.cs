using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;
using Notifications.API.Services;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Notifications.API.IntegrationEvents;

/// <summary>Catalog events that notify followers. Duplicates are ignored by the inbox's unique (user, event) index.</summary>
public sealed class CatalogNotificationHandlers(NotificationFanOut fanOut) :
    IIntegrationEventHandler<EpisodeAiredIntegrationEvent>,
    IIntegrationEventHandler<SeasonAnnouncedIntegrationEvent>,
    IIntegrationEventHandler<ShowStatusChangedIntegrationEvent>
{
    public Task HandleAsync(EpisodeAiredIntegrationEvent e, CancellationToken ct) =>
        fanOut.NotifyFollowersAsync(e, new NotificationContent(
            NotificationKind.NewEpisode, e.ShowId, e.EpisodeId,
            e.ShowTitle,
            $"S{e.SeasonNumber:00}E{e.EpisodeNumber:00} \"{e.Title}\" is out now.",
            $"/shows/{e.ShowId}"), ct);

    public Task HandleAsync(SeasonAnnouncedIntegrationEvent e, CancellationToken ct) =>
        fanOut.NotifyFollowersAsync(e, new NotificationContent(
            NotificationKind.NewSeason, e.ShowId, null,
            $"{e.ShowTitle}: season {e.SeasonNumber} announced",
            e.PremieresAtUtc is { } date ? $"It premieres on {date:d MMMM yyyy}." : "Premiere date to be confirmed.",
            $"/shows/{e.ShowId}"), ct);

    public Task HandleAsync(ShowStatusChangedIntegrationEvent e, CancellationToken ct)
    {
        var body = e.NewStatus switch
        {
            ShowStatus.Renewed => $"{e.ShowTitle} has been renewed.",
            ShowStatus.Cancelled => $"{e.ShowTitle} has been cancelled.",
            ShowStatus.Ended => $"{e.ShowTitle} has ended.",
            _ => null
        };

        return body is null
            ? Task.CompletedTask
            : fanOut.NotifyFollowersAsync(e, new NotificationContent(
                NotificationKind.StatusChange, e.ShowId, null, e.ShowTitle, body, $"/shows/{e.ShowId}"), ct);
    }
}

/// <summary>Keeps the local follower list and user details current.</summary>
public sealed class UserEventHandlers(NotificationsContext db, NotificationHubPush push, TimeProvider time) :
    IIntegrationEventHandler<ShowFollowedIntegrationEvent>,
    IIntegrationEventHandler<ShowUnfollowedIntegrationEvent>,
    IIntegrationEventHandler<UserRegisteredIntegrationEvent>,
    IIntegrationEventHandler<UserProfileUpdatedIntegrationEvent>,
    IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public async Task HandleAsync(ShowFollowedIntegrationEvent e, CancellationToken ct)
    {
        if (await db.Followers.FindAsync([e.ShowId, e.UserId], ct) is null)
        {
            db.Followers.Add(new Follower { ShowId = e.ShowId, UserId = e.UserId });
            await db.SaveChangesAsync(ct);
        }
    }

    public Task HandleAsync(ShowUnfollowedIntegrationEvent e, CancellationToken ct) =>
        db.Followers.Where(f => f.ShowId == e.ShowId && f.UserId == e.UserId).ExecuteDeleteAsync(ct);

    public async Task HandleAsync(UserRegisteredIntegrationEvent e, CancellationToken ct)
    {
        var prefs = await GetOrCreatePreferencesAsync(e.UserId, ct);
        prefs.Email ??= e.Email;
        await db.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(UserProfileUpdatedIntegrationEvent e, CancellationToken ct)
    {
        if (await db.HasProcessedAsync(e, nameof(UserProfileUpdatedIntegrationEvent), ct))
        {
            return;
        }

        var prefs = await GetOrCreatePreferencesAsync(e.UserId, ct);
        prefs.TimeZone = e.TimeZone;
        prefs.Email = e.Email ?? prefs.Email;
        prefs.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        db.MarkProcessed(e, nameof(UserProfileUpdatedIntegrationEvent));
        await db.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        foreach (var device in await db.Devices.Where(d => d.UserId == e.UserId).ToListAsync(ct))
        {
            await push.UnregisterAsync(device, ct);
        }

        await db.Devices.Where(d => d.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Deliveries.Where(d => d.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Inbox.Where(i => i.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Followers.Where(f => f.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.ShowPreferences.Where(p => p.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Preferences.Where(p => p.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }

    private async Task<NotificationPreferences> GetOrCreatePreferencesAsync(string userId, CancellationToken ct)
    {
        var prefs = await db.Preferences.FindAsync([userId], ct);
        if (prefs is null)
        {
            prefs = new NotificationPreferences { UserId = userId, UpdatedAtUtc = time.GetUtcNow().UtcDateTime };
            db.Preferences.Add(prefs);
        }
        return prefs;
    }
}
