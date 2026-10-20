using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;
using TvTracker.EventBus;

namespace Notifications.API.Services;

public record NotificationContent(NotificationKind Kind, Guid ShowId, Guid? EpisodeId, string Title, string Body, string Link);

/// <summary>
/// Turns one catalog event into an inbox item per interested follower plus one queued delivery per channel.
/// The delivery worker sends the deliveries; this only writes rows, in batches.
/// </summary>
public sealed class NotificationFanOut(NotificationsContext db, TimeProvider time, ILogger<NotificationFanOut> logger)
{
    private const int BatchSize = 500;

    public async Task NotifyFollowersAsync(IntegrationEvent source, NotificationContent content, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        var followers = await db.Followers.Where(f => f.ShowId == content.ShowId).Select(f => f.UserId).ToListAsync(ct);
        if (followers.Count == 0)
        {
            return;
        }

        var muted = (await db.ShowPreferences
            .Where(p => p.ShowId == content.ShowId && p.Muted)
            .Select(p => p.UserId)
            .ToListAsync(ct)).ToHashSet();

        // Users already notified for this event (the event was redelivered after a partial run).
        var alreadyNotified = (await db.Inbox
            .Where(i => i.SourceEventId == source.Id)
            .Select(i => i.UserId)
            .ToListAsync(ct)).ToHashSet();

        var recipients = followers.Where(u => !muted.Contains(u) && !alreadyNotified.Contains(u)).ToList();
        var created = 0;

        foreach (var batch in recipients.Chunk(BatchSize))
        {
            var prefs = await db.Preferences.AsNoTracking()
                .Where(p => batch.Contains(p.UserId))
                .ToDictionaryAsync(p => p.UserId, ct);

            var platforms = (await db.Devices.AsNoTracking()
                .Where(d => batch.Contains(d.UserId))
                .Select(d => new { d.UserId, d.Platform })
                .ToListAsync(ct))
                .ToLookup(d => d.UserId, d => d.Platform);

            foreach (var userId in batch)
            {
                var pref = prefs.GetValueOrDefault(userId) ?? new NotificationPreferences { UserId = userId };
                if (!Wants(pref, content.Kind))
                {
                    continue;
                }

                var item = new InboxItem
                {
                    Id = Guid.CreateVersion7(),
                    UserId = userId,
                    Kind = content.Kind,
                    Title = content.Title,
                    Body = content.Body,
                    Link = content.Link,
                    ShowId = content.ShowId,
                    EpisodeId = content.EpisodeId,
                    SourceEventId = source.Id,
                    CreatedAtUtc = now
                };
                db.Inbox.Add(item);
                created++;

                var sendAt = QuietHours.NextAllowedUtc(pref, now);
                var userPlatforms = platforms[userId].ToList();

                if (pref.PushEnabled && userPlatforms.Any(p => p is DevicePlatform.Android or DevicePlatform.iOS))
                {
                    AddDelivery(item, DeliveryChannel.Push, sendAt);
                }
                if (pref.WebPushEnabled && userPlatforms.Contains(DevicePlatform.Web))
                {
                    AddDelivery(item, DeliveryChannel.WebPush, sendAt);
                }
                if (pref.EmailEnabled && !string.IsNullOrWhiteSpace(pref.Email))
                {
                    AddDelivery(item, DeliveryChannel.Email, sendAt);
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        logger.LogInformation("{Kind} for show {ShowId}: {Count} notifications queued", content.Kind, content.ShowId, created);
    }

    private void AddDelivery(InboxItem item, DeliveryChannel channel, DateTime sendAt) =>
        db.Deliveries.Add(new Delivery
        {
            Id = Guid.CreateVersion7(),
            InboxItemId = item.Id,
            UserId = item.UserId,
            Channel = channel,
            Status = DeliveryStatus.Pending,
            ScheduledForUtc = sendAt
        });

    private static bool Wants(NotificationPreferences pref, NotificationKind kind) => kind switch
    {
        NotificationKind.NewEpisode => pref.NewEpisodes,
        NotificationKind.NewSeason => pref.NewSeasons,
        NotificationKind.StatusChange => pref.StatusChanges,
        _ => false
    };
}
