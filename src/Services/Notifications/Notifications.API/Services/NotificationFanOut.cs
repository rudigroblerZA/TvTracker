using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;
using TvTracker;
using TvTracker.EventBus;

namespace Notifications.API.Services;

/// <param name="Flag">A feature flag the alert is behind, evaluated per follower (their id, and their email when
/// Notifications has it), so a flag targeted at some users holds it back from the others only.</param>
public record NotificationContent(NotificationKind Kind, Guid ShowId, Guid? EpisodeId, string Title, string Body, string Link, string? Flag = null);

/// <param name="HeldBack">Followers who wanted it but the flag was off for; a caller that would hear the event again
/// (a crossover's hourly re-send) leaves it unprocessed while there are any, so they get it once the flag turns on.</param>
public record FanOutResult(int Created, int HeldBack);

/// <summary>
/// Turns one catalog event into an inbox item per interested follower plus one queued delivery per channel.
/// The delivery worker sends the deliveries; this only writes rows, in batches.
/// </summary>
public sealed class NotificationFanOut(NotificationsContext db, CachedFlags flags, TimeProvider time, ILogger<NotificationFanOut> logger)
{
    private const int BatchSize = 500;
    private const int ConcurrentFlagChecks = 16;

    public async Task<FanOutResult> NotifyFollowersAsync(IntegrationEvent source, NotificationContent content, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        var followers = await db.Followers.Where(f => f.ShowId == content.ShowId).Select(f => f.UserId).ToListAsync(ct);
        if (followers.Count == 0)
        {
            return new(0, 0);
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
        var (created, heldBack) = (0, 0);

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

            var wanting = batch
                .Select(userId => prefs.GetValueOrDefault(userId) ?? new NotificationPreferences { UserId = userId })
                .Where(pref => Wants(pref, content.Kind))
                .ToList();
            if (content.Flag is { } flag)
            {
                var allowed = await AllowedAsync(flag, wanting, ct);
                heldBack += wanting.Count - allowed.Count;
                wanting = wanting.Where(p => allowed.Contains(p.UserId)).ToList();
            }

            foreach (var pref in wanting)
            {
                var userId = pref.UserId;

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
                // With the weekly digest on, email comes once a week instead (WeeklyDigest); push carries on.
                if (pref.EmailEnabled && !pref.WeeklyDigest && !string.IsNullOrWhiteSpace(pref.Email))
                {
                    AddDelivery(item, DeliveryChannel.Email, sendAt);
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        logger.LogInformation("{Kind} for show {ShowId}: {Count} notifications queued, {HeldBack} held back by the flag",
            content.Kind, content.ShowId, created, heldBack);
        return new(created, heldBack);
    }

    /// <summary>
    /// Who the flag is on for, checked a few at a time: each follower is their own evaluation context, so a large
    /// show's fan-out would otherwise wait on thousands of flagd round trips one after another.
    /// </summary>
    private async Task<HashSet<string>> AllowedAsync(string flag, List<NotificationPreferences> prefs, CancellationToken ct)
    {
        var allowed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(prefs, new ParallelOptions { MaxDegreeOfParallelism = ConcurrentFlagChecks, CancellationToken = ct },
            async (pref, token) =>
            {
                if (await flags.IsEnabledAsync(flag, FeatureFlags.ContextForUser(pref.UserId, pref.Email), token))
                {
                    allowed.Add(pref.UserId);
                }
            });
        return [.. allowed];
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

    internal static bool Wants(NotificationPreferences pref, NotificationKind kind) => kind switch
    {
        NotificationKind.NewEpisode => pref.NewEpisodes,
        NotificationKind.NewSeason => pref.NewSeasons,
        NotificationKind.SeasonVerdict => pref.SeasonVerdicts,
        NotificationKind.Crossover => pref.NewEpisodes,
        NotificationKind.StatusChange => pref.StatusChanges,
        _ => false
    };
}
