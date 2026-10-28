using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Azure;
using Azure.Communication.Email;
using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;
using OpenFeature;
using TvTracker;

namespace Notifications.API.Services;

public sealed record DigestLine(string Text, string? Link);

public sealed record DigestSection(string Heading, IReadOnlyList<DigestLine> Lines);

/// <summary>One user's week: what's coming on their shows, and what they were told since the last digest.</summary>
public sealed record Digest(string Subject, IReadOnlyList<DigestSection> Sections)
{
    public bool IsEmpty => Sections.Count == 0;

    public string Text() =>
        string.Join("\n\n", Sections.Select(s => $"{s.Heading}\n" + string.Join("\n", s.Lines.Select(l => $"- {l.Text}"))));

    /// <param name="baseUrl">The web app's address, so in-app links work from a mail client.</param>
    public string Html(string baseUrl)
    {
        var html = new StringBuilder();
        foreach (var section in Sections)
        {
            html.Append($"<h2 style=\"font-size:16px;margin:20px 0 6px\">{WebUtility.HtmlEncode(section.Heading)}</h2><ul style=\"padding-left:18px;margin:0\">");
            foreach (var line in section.Lines)
            {
                var text = WebUtility.HtmlEncode(line.Text);
                html.Append(line.Link is { } link
                    ? $"<li><a href=\"{WebUtility.HtmlEncode(baseUrl.TrimEnd('/') + link)}\">{text}</a></li>"
                    : $"<li>{text}</li>");
            }
            html.Append("</ul>");
        }
        return html.ToString();
    }
}

/// <summary>An episode from Catalog.API's schedule (the parts the digest uses).</summary>
public sealed record ScheduledEpisode(Guid ShowId, string ShowTitle, ScheduleEpisode Episode);

public sealed record ScheduleEpisode(int SeasonNumber, int EpisodeNumber, string Title, DateTimeOffset? AirsAtUtc);

/// <summary>Reads the schedule from Catalog.API, as any client does.</summary>
public sealed class CatalogSchedule(HttpClient http)
{
    public async Task<List<ScheduledEpisode>> GetAsync(IReadOnlyCollection<Guid> showIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (showIds.Count == 0)
        {
            return [];
        }
        var url = $"api/v1/schedule?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}" +
                  $"&showIds={string.Join(',', showIds.Take(200))}";
        return await http.GetFromJsonAsync<List<ScheduledEpisode>>(url, ct) ?? [];
    }
}

/// <summary>
/// The weekly digest: one email instead of an email per alert (push alerts carry on), at the day and hour the user
/// picks, in their time zone.
/// <list type="bullet">
/// <item><b>Coming up:</b> the next 7 days of episodes of the shows they follow and haven't muted, from Catalog.API's
/// schedule, premieres marked.</item>
/// <item><b>Since the last one:</b> their inbox since the last digest (a week at most): crossovers, season verdicts,
/// new seasons and status changes as they were sent, and new episodes counted per show.</item>
/// </list>
/// Up Next and what people they follow finished aren't in it: those live in Tracking and Social, which Notifications
/// can't read for a user without their token.
/// </summary>
public sealed class WeeklyDigest(NotificationsContext db, CatalogSchedule schedule, Branding branding, TimeProvider time)
{
    private const int MaxComingUp = 30;
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public async Task<Digest> BuildAsync(NotificationPreferences prefs, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var tz = TimeZoneInfo.TryFindSystemTimeZoneById(prefs.TimeZone, out var found) ? found : TimeZoneInfo.Utc;
        var since = prefs.LastDigestAtUtc is { } last && now.UtcDateTime - last < Week ? last : now.UtcDateTime - Week;
        var sections = new List<DigestSection>();

        var muted = db.ShowPreferences.Where(p => p.UserId == prefs.UserId && p.Muted).Select(p => p.ShowId);
        var shows = await db.Followers.Where(f => f.UserId == prefs.UserId && !muted.Contains(f.ShowId)).Select(f => f.ShowId).ToListAsync(ct);
        var coming = (await schedule.GetAsync(shows, now, now + Week, ct))
            .Where(e => e.Episode.AirsAtUtc is not null)
            .OrderBy(e => e.Episode.AirsAtUtc)
            .ToList();
        if (coming.Count > 0)
        {
            var lines = coming.Take(MaxComingUp).Select(e =>
            {
                var local = TimeZoneInfo.ConvertTime(e.Episode.AirsAtUtc!.Value, tz);
                var premiere = e.Episode.EpisodeNumber == 1 ? (e.Episode.SeasonNumber == 1 ? " · series premiere" : " · season premiere") : "";
                return new DigestLine(
                    $"{local.ToString("ddd d MMM", CultureInfo.InvariantCulture)} · {e.ShowTitle} S{e.Episode.SeasonNumber:00}E{e.Episode.EpisodeNumber:00} \"{e.Episode.Title}\"{premiere}",
                    $"/shows/{e.ShowId}");
            }).ToList();
            if (coming.Count > MaxComingUp)
            {
                lines.Add(new DigestLine($"and {coming.Count - MaxComingUp} more", "/calendar"));
            }
            sections.Add(new DigestSection("Coming up this week", lines));
        }

        var inbox = await db.Inbox.AsNoTracking()
            .Where(i => i.UserId == prefs.UserId && i.CreatedAtUtc > since)
            .OrderBy(i => i.CreatedAtUtc)
            .ToListAsync(ct);
        AddItems(sections, "Crossovers ahead", inbox, NotificationKind.Crossover);
        AddItems(sections, "How seasons did", inbox, NotificationKind.SeasonVerdict);
        AddItems(sections, "News about your shows", inbox, NotificationKind.NewSeason, NotificationKind.StatusChange);
        var aired = inbox.Where(i => i.Kind == NotificationKind.NewEpisode)
            .GroupBy(i => (i.ShowId, i.Title))
            .OrderByDescending(g => g.Count())
            .Select(g => new DigestLine($"{g.Key.Title}: {g.Count()} new {(g.Count() == 1 ? "episode" : "episodes")}", $"/shows/{g.Key.ShowId}"))
            .ToList();
        if (aired.Count > 0)
        {
            sections.Add(new DigestSection("Out since last week", aired));
        }

        var episodes = coming.Count;
        var subject = episodes > 0
            ? $"{branding.AppName}: your week, {episodes} {(episodes == 1 ? "episode" : "episodes")} coming up"
            : $"{branding.AppName}: your week";
        return new Digest(subject, sections);
    }

    private static void AddItems(List<DigestSection> sections, string heading, List<InboxItem> inbox, params NotificationKind[] kinds)
    {
        var lines = inbox.Where(i => kinds.Contains(i.Kind)).Select(i => new DigestLine($"{i.Title}: {i.Body}", i.Link)).ToList();
        if (lines.Count > 0)
        {
            sections.Add(new DigestSection(heading, lines));
        }
    }
}

public sealed class DigestOptions
{
    /// <summary>The web app's address, for links in the email.</summary>
    public string BaseUrl { get; set; } = "https://localhost:7120";
}

/// <summary>
/// Sends each user's digest when it's due: on their day, from their hour, in their time zone, at most once in six days
/// (so a late run or a changed day can't send two in one week). A week with nothing in it sends nothing, but counts as
/// sent. Email logs instead of sending until <c>Email:ConnectionString</c> and <c>Email:SenderAddress</c> are set.
/// </summary>
public sealed class DigestWorker(IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider time, ILogger<DigestWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinGap = TimeSpan.FromDays(6);

    private readonly EmailOptions _email = configuration.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();
    private readonly DigestOptions _options = configuration.GetSection("Digest").Get<DigestOptions>() ?? new DigestOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await SendDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Weekly digests failed; trying again in {Minutes} minutes", Interval.TotalMinutes);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SendDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        // Checked once a run, without a user: it can't target some users only.
        if (!await scope.ServiceProvider.GetRequiredService<IFeatureClient>()
                .GetBooleanValueAsync(FeatureFlags.WeeklyDigest, FeatureFlags.DefaultValue, cancellationToken: ct))
        {
            return;
        }
        var db = scope.ServiceProvider.GetRequiredService<NotificationsContext>();
        var digests = scope.ServiceProvider.GetRequiredService<WeeklyDigest>();
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = now - MinGap;

        var candidates = await db.Preferences
            .Where(p => p.WeeklyDigest && p.Email != null && (p.LastDigestAtUtc == null || p.LastDigestAtUtc < cutoff))
            .ToListAsync(ct);
        var sent = 0;
        foreach (var prefs in candidates.Where(p => IsDue(p, now)))
        {
            try
            {
                var digest = await digests.BuildAsync(prefs, ct);
                if (!digest.IsEmpty)
                {
                    await SendAsync(prefs.Email!, digest, ct);
                    sent++;
                }
                prefs.LastDigestAtUtc = now;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One user's failure (the catalog down, a bad address) leaves the rest; theirs is tried again next run.
                logger.LogWarning(ex, "Weekly digest for {UserId} failed", prefs.UserId);
            }
        }
        if (sent > 0)
        {
            logger.LogInformation("Sent {Count} weekly digests", sent);
        }
    }

    /// <summary>Their day, from their hour, in their time zone.</summary>
    internal static bool IsDue(NotificationPreferences prefs, DateTime nowUtc)
    {
        var tz = TimeZoneInfo.TryFindSystemTimeZoneById(prefs.TimeZone, out var found) ? found : TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        return local.DayOfWeek == prefs.DigestDay && local.Hour >= prefs.DigestHour;
    }

    private async Task SendAsync(string email, Digest digest, CancellationToken ct)
    {
        if (!_email.IsConfigured)
        {
            logger.LogInformation("[email not configured] Digest to {Email}: {Subject}\n{Text}", email, digest.Subject, digest.Text());
            return;
        }
        var client = new EmailClient(_email.ConnectionString);
        await client.SendAsync(WaitUntil.Started, _email.SenderAddress, email, digest.Subject, digest.Html(_options.BaseUrl), digest.Text(), ct);
    }
}
