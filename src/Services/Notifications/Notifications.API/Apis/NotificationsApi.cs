using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;
using Notifications.API.Services;

namespace Notifications.API.Apis;

public record PreferencesDto(
    bool NewEpisodes,
    bool NewSeasons,
    bool StatusChanges,
    bool PushEnabled,
    bool WebPushEnabled,
    bool EmailEnabled,
    string? Email,
    TimeOnly? QuietHoursStart,
    TimeOnly? QuietHoursEnd,
    string TimeZone);

public record UpdatePreferencesRequest(
    bool NewEpisodes,
    bool NewSeasons,
    bool StatusChanges,
    bool PushEnabled,
    bool WebPushEnabled,
    bool EmailEnabled,
    TimeOnly? QuietHoursStart,
    TimeOnly? QuietHoursEnd);

public record ShowPreferenceDto(Guid ShowId, bool Muted);

public record UpdateShowPreferenceRequest(bool Muted);

/// <param name="Token">FCM/APNs token for Android/iOS, or the push subscription endpoint for web.</param>
public record RegisterDeviceRequest(DevicePlatform Platform, string Token, string? P256dh, string? Auth);

public record DeviceDto(Guid Id, DevicePlatform Platform, DateTime CreatedAtUtc);

public record InboxItemDto(Guid Id, NotificationKind Kind, string Title, string Body, string? Link, Guid ShowId, Guid? EpisodeId, DateTime CreatedAtUtc, bool IsRead);

public record InboxPageDto(IReadOnlyList<InboxItemDto> Items, int UnreadCount);

public static class NotificationsApi
{
    public static IEndpointRouteBuilder MapNotificationsApi(this IEndpointRouteBuilder app)
    {
        // The web app needs the VAPID public key before the user subscribes.
        app.MapGet("/api/v1/webpush/public-key", (IConfiguration config) =>
        {
            var key = config["WebPush:PublicKey"];
            return string.IsNullOrWhiteSpace(key) ? Results.NotFound() : Results.Ok(new { publicKey = key });
        });

        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/preferences", GetPreferencesAsync);
        api.MapPut("/preferences", UpdatePreferencesAsync);
        api.MapGet("/shows/{showId:guid}/preferences", GetShowPreferenceAsync);
        api.MapPut("/shows/{showId:guid}/preferences", UpdateShowPreferenceAsync);

        api.MapGet("/devices", GetDevicesAsync);
        api.MapPost("/devices", RegisterDeviceAsync);
        api.MapDelete("/devices/{id:guid}", RemoveDeviceAsync);

        api.MapGet("/inbox", GetInboxAsync);
        api.MapPost("/inbox/{id:guid}/read", MarkReadAsync);
        api.MapPost("/inbox/read-all", MarkAllReadAsync);

        api.MapGet("/me/export", ExportAsync);

        return app;
    }

    private static async Task<Ok<PreferencesDto>> GetPreferencesAsync(ClaimsPrincipal user, NotificationsContext db, CancellationToken ct)
    {
        var prefs = await GetOrCreateAsync(user, db, ct);
        return TypedResults.Ok(ToDto(prefs));
    }

    private static async Task<Ok<PreferencesDto>> UpdatePreferencesAsync(
        UpdatePreferencesRequest request, ClaimsPrincipal user, NotificationsContext db, CancellationToken ct)
    {
        var prefs = await GetOrCreateAsync(user, db, ct);

        prefs.NewEpisodes = request.NewEpisodes;
        prefs.NewSeasons = request.NewSeasons;
        prefs.StatusChanges = request.StatusChanges;
        prefs.PushEnabled = request.PushEnabled;
        prefs.WebPushEnabled = request.WebPushEnabled;
        prefs.EmailEnabled = request.EmailEnabled;
        prefs.QuietHoursStart = request.QuietHoursStart;
        prefs.QuietHoursEnd = request.QuietHoursEnd;
        prefs.Email = user.GetEmail() ?? prefs.Email;
        prefs.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(prefs));
    }

    private static async Task<Ok<ShowPreferenceDto>> GetShowPreferenceAsync(Guid showId, ClaimsPrincipal user, NotificationsContext db, CancellationToken ct)
    {
        var pref = await db.ShowPreferences.FindAsync([user.GetUserId(), showId], ct);
        return TypedResults.Ok(new ShowPreferenceDto(showId, pref?.Muted ?? false));
    }

    private static async Task<Ok<ShowPreferenceDto>> UpdateShowPreferenceAsync(
        Guid showId, UpdateShowPreferenceRequest request, ClaimsPrincipal user, NotificationsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var pref = await db.ShowPreferences.FindAsync([userId, showId], ct);
        if (pref is null)
        {
            pref = new ShowPreference { UserId = userId, ShowId = showId };
            db.ShowPreferences.Add(pref);
        }

        pref.Muted = request.Muted;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new ShowPreferenceDto(showId, pref.Muted));
    }

    private static async Task<Ok<List<DeviceDto>>> GetDevicesAsync(ClaimsPrincipal user, NotificationsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.UserId == userId)
            .Select(d => new DeviceDto(d.Id, d.Platform, d.CreatedAtUtc))
            .ToListAsync(ct);
        return TypedResults.Ok(devices);
    }

    /// <summary>Registers (or re-registers) a device. A token moves to the signed-in user if it was registered by someone else.</summary>
    private static async Task<Results<Ok<DeviceDto>, ValidationProblem>> RegisterDeviceAsync(
        RegisterDeviceRequest request, ClaimsPrincipal user, NotificationsContext db, NotificationHubPush push, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 1024)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["token"] = ["A device token is required."] });
        }
        if (request.Platform == DevicePlatform.Web && (string.IsNullOrWhiteSpace(request.P256dh) || string.IsNullOrWhiteSpace(request.Auth)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["p256dh"] = ["Web push subscriptions need p256dh and auth keys."] });
        }

        var userId = user.GetUserId();
        var now = DateTime.UtcNow;

        var device = await db.Devices.SingleOrDefaultAsync(d => d.Token == request.Token, ct);
        if (device is null)
        {
            device = new Device { Id = Guid.CreateVersion7(), UserId = userId, Token = request.Token, CreatedAtUtc = now };
            db.Devices.Add(device);
        }

        device.UserId = userId;
        device.Platform = request.Platform;
        device.P256dh = request.P256dh;
        device.Auth = request.Auth;
        device.LastSeenAtUtc = now;

        await db.SaveChangesAsync(ct);
        await push.RegisterAsync(device, ct);

        return TypedResults.Ok(new DeviceDto(device.Id, device.Platform, device.CreatedAtUtc));
    }

    private static async Task<Results<NoContent, NotFound>> RemoveDeviceAsync(
        Guid id, ClaimsPrincipal user, NotificationsContext db, NotificationHubPush push, CancellationToken ct)
    {
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == id && d.UserId == user.GetUserId(), ct);
        if (device is null)
        {
            return TypedResults.NotFound();
        }

        await push.UnregisterAsync(device, ct);
        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<InboxPageDto>> GetInboxAsync(
        ClaimsPrincipal user, NotificationsContext db, bool unreadOnly = false, int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Inbox.AsNoTracking().Where(i => i.UserId == userId);
        if (unreadOnly)
        {
            query = query.Where(i => i.ReadAtUtc == null);
        }

        var items = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new InboxItemDto(i.Id, i.Kind, i.Title, i.Body, i.Link, i.ShowId, i.EpisodeId, i.CreatedAtUtc, i.ReadAtUtc != null))
            .ToListAsync(ct);

        var unread = await db.Inbox.CountAsync(i => i.UserId == userId && i.ReadAtUtc == null, ct);

        return TypedResults.Ok(new InboxPageDto(items, unread));
    }

    private static async Task<Results<NoContent, NotFound>> MarkReadAsync(Guid id, ClaimsPrincipal user, NotificationsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var updated = await db.Inbox
            .Where(i => i.Id == id && i.UserId == userId && i.ReadAtUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReadAtUtc, DateTime.UtcNow), ct);

        return updated > 0 || await db.Inbox.AnyAsync(i => i.Id == id && i.UserId == userId, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static async Task<NoContent> MarkAllReadAsync(ClaimsPrincipal user, NotificationsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await db.Inbox
            .Where(i => i.UserId == userId && i.ReadAtUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReadAtUtc, DateTime.UtcNow), ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ExportAsync(ClaimsPrincipal user, NotificationsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var prefs = await db.Preferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);

        return Results.Ok(new
        {
            preferences = prefs is null ? null : ToDto(prefs),
            mutedShows = await db.ShowPreferences.AsNoTracking().Where(p => p.UserId == userId && p.Muted).Select(p => p.ShowId).ToListAsync(ct),
            devices = await db.Devices.AsNoTracking().Where(d => d.UserId == userId)
                .Select(d => new { d.Platform, d.CreatedAtUtc, d.LastSeenAtUtc }).ToListAsync(ct),
            inbox = await db.Inbox.AsNoTracking().Where(i => i.UserId == userId).OrderBy(i => i.CreatedAtUtc)
                .Select(i => new { i.Title, i.Body, i.CreatedAtUtc, i.ReadAtUtc }).ToListAsync(ct)
        });
    }

    private static async Task<NotificationPreferences> GetOrCreateAsync(ClaimsPrincipal user, NotificationsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var prefs = await db.Preferences.FindAsync([userId], ct);
        if (prefs is null)
        {
            prefs = new NotificationPreferences { UserId = userId, Email = user.GetEmail(), UpdatedAtUtc = DateTime.UtcNow };
            db.Preferences.Add(prefs);
            await db.SaveChangesAsync(ct);
        }
        return prefs;
    }

    private static PreferencesDto ToDto(NotificationPreferences p) =>
        new(p.NewEpisodes, p.NewSeasons, p.StatusChanges, p.PushEnabled, p.WebPushEnabled, p.EmailEnabled,
            p.Email, p.QuietHoursStart, p.QuietHoursEnd, p.TimeZone);
}
