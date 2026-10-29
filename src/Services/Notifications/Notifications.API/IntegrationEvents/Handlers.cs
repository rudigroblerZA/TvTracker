using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;
using Notifications.API.Services;
using OpenFeature;
using System.Globalization;
using TvTracker;
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

/// <summary>
/// How a season that just ended did, from Analytics (<see cref="SeasonVerdictIntegrationEvent"/>), for the show's
/// followers.
/// <list type="bullet">
/// <item><b>Once per season:</b> Analytics sends a verdict when it first appears, and again only if it dropped out of
/// the show's document for a run. Each send has its own event Id but the same
/// <see cref="SeasonVerdictIntegrationEvent.VerdictId"/>, which stands in as the event's identity here, for the inbox
/// check and for the inbox items' source.</item>
/// <item><b>Preference:</b> "new seasons", which is about a show's seasons; a preference of its own would need a new
/// column, and this service still uses EnsureCreated.</item>
/// <item><b>Flag:</b> evaluated without a user, so it can't target some followers only. A verdict that arrives while
/// it's off isn't marked processed, but it isn't sent again either, so it's dropped.</item>
/// </list>
/// </summary>
public sealed class AnalyticsNotificationHandlers(NotificationsContext db, NotificationFanOut fanOut, IFeatureClient flags)
    : IIntegrationEventHandler<SeasonVerdictIntegrationEvent>
{
    public async Task HandleAsync(SeasonVerdictIntegrationEvent e, CancellationToken ct)
    {
        // Events sent before VerdictId existed keep their own Id.
        var verdict = e.VerdictId == Guid.Empty ? e : e with { Id = e.VerdictId };
        if (await db.HasProcessedAsync(verdict, nameof(AnalyticsNotificationHandlers), ct)
            || !await flags.GetBooleanValueAsync(FeatureFlags.SeasonVerdict, FeatureFlags.DefaultValue, cancellationToken: ct))
        {
            return;
        }

        await fanOut.NotifyFollowersAsync(verdict, new NotificationContent(
            NotificationKind.SeasonVerdict, e.ShowId, null,
            $"{e.ShowTitle}: how season {e.SeasonNumber} did",
            SeasonVerdictText(e),
            $"/shows/{e.ShowId}"), ct);

        db.MarkProcessed(verdict, nameof(AnalyticsNotificationHandlers));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"Its best season yet, rated 8.6 against 8.1 for the others. The finale was a high point. 12 of 20 …"</summary>
    internal static string SeasonVerdictText(SeasonVerdictIntegrationEvent e)
    {
        var rating = e.Rating.ToString("0.0", CultureInfo.InvariantCulture);
        var others = e.OthersMedian?.ToString("0.0", CultureInfo.InvariantCulture);
        var text = e.Verdict switch
        {
            SeasonVerdicts.BestYet => $"Its best season yet, rated {rating} against {others} for the others.",
            SeasonVerdicts.WeakestYet => $"Its weakest season yet, rated {rating} against {others} for the others.",
            SeasonVerdicts.Above => $"One of its better seasons, rated {rating} against {others} for the others.",
            SeasonVerdicts.Below => $"One of its weaker seasons, rated {rating} against {others} for the others.",
            SeasonVerdicts.InLine => $"Rated {rating}, in line with its other seasons ({others}).",
            SeasonVerdicts.FirstSeason => $"Its first season rated {rating}.",
            _ => $"Rated {rating}."
        };
        text += e.Finale switch
        {
            FinaleVerdicts.Strong => " The finale was a high point.",
            FinaleVerdicts.Weak => " The finale rated below the season.",
            _ => ""
        };
        if (e.Started is { } started && e.Finished is { } finished)
        {
            text += $" {finished} of the {started} viewers here who started it have finished it.";
        }
        return text;
    }
}

/// <summary>
/// A crossover across a universe's shows starting soon (<see cref="CrossoverScheduledIntegrationEvent"/>), for the
/// followers of any of its shows: one inbox item each, however many of the shows they follow.
/// <list type="bullet">
/// <item><b>Once per crossover:</b> Catalog.API sends it every hour while it's ahead, each time with a new event Id;
/// <see cref="CrossoverScheduledIntegrationEvent.CrossoverId"/> stands in as its identity, as a season verdict's does.
/// Fanning out per show with that one identity skips anyone already told through another of the shows.</item>
/// <item><b>When, not a date:</b> the text says how many days away it starts, since one text goes to followers in
/// every time zone and a US evening is the next day in UTC.</item>
/// <item><b>Preference:</b> "new episodes", since it's about episodes airing.</item>
/// </list>
/// </summary>
public sealed class CrossoverNotificationHandler(NotificationsContext db, NotificationFanOut fanOut, IFeatureClient flags, TimeProvider time)
    : IIntegrationEventHandler<CrossoverScheduledIntegrationEvent>
{
    public async Task HandleAsync(CrossoverScheduledIntegrationEvent e, CancellationToken ct)
    {
        var crossover = e with { Id = e.CrossoverId };
        if (e.Parts.Count == 0
            || await db.HasProcessedAsync(crossover, nameof(CrossoverNotificationHandler), ct)
            || !await flags.GetBooleanValueAsync(FeatureFlags.CrossoverAlerts, FeatureFlags.DefaultValue, cancellationToken: ct))
        {
            return;
        }

        var body = CrossoverText(e, time.GetUtcNow());
        foreach (var show in e.Parts.Select(p => p.ShowId).Distinct())
        {
            await fanOut.NotifyFollowersAsync(crossover, new NotificationContent(
                NotificationKind.Crossover, show, null, $"Crossover ahead: {e.Name}", body, $"/universes/{e.UniverseId}"), ct);
        }

        db.MarkProcessed(crossover, nameof(CrossoverNotificationHandler));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"Airs across Chicago Fire, Chicago Med and Chicago P.D., starting in 6 days."</summary>
    internal static string CrossoverText(CrossoverScheduledIntegrationEvent e, DateTimeOffset now)
    {
        var shows = e.Parts.Select(p => p.ShowTitle).Distinct().ToList();
        var across = shows.Count == 1 ? shows[0] : $"{string.Join(", ", shows[..^1])} and {shows[^1]}";
        var days = (int)Math.Ceiling((e.Parts[0].AirsAtUtc - now).TotalDays);
        var when = days <= 0 ? "today" : days == 1 ? "tomorrow" : $"in {days} days";
        return $"Airs across {across}, starting {when}. Catch up first: the watch order has every episode in sequence.";
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
