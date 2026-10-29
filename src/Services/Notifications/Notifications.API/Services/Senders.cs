using Azure;
using Azure.Communication.Email;
using Microsoft.Azure.NotificationHubs;
using Microsoft.EntityFrameworkCore;
using Notifications.API.Data;
using System.Net;
using System.Text.Json;
using WebPush;

namespace Notifications.API.Services;

public record SendResult(bool Success, string? Error = null)
{
    public static readonly SendResult Ok = new(true);
}

/// <summary>Sends one delivery on one channel.</summary>
public interface IChannelSender
{
    DeliveryChannel Channel { get; }

    Task<SendResult> SendAsync(Delivery delivery, CancellationToken ct);
}

public sealed class NotificationHubOptions
{
    public string? ConnectionString { get; set; }
    public string HubName { get; set; } = "tvtracker";
}

/// <summary>
/// Mobile push through Azure Notification Hubs. Each device is an installation tagged "user:{id}",
/// so one send reaches all of a user's phones. Without a connection string it only logs.
/// </summary>
public sealed class NotificationHubPush(NotificationsContext db, IConfiguration configuration, ILogger<NotificationHubPush> logger) : IChannelSender
{
    private readonly NotificationHubClient? _client = CreateClient(configuration);

    public DeliveryChannel Channel => DeliveryChannel.Push;

    public async Task RegisterAsync(Device device, CancellationToken ct)
    {
        if (_client is null || device.Platform == DevicePlatform.Web)
        {
            logger.LogInformation("Notification Hubs not configured; device {DeviceId} stored only", device.Id);
            return;
        }

        var installation = new Installation
        {
            InstallationId = device.Id.ToString(),
            Platform = device.Platform == DevicePlatform.Android ? NotificationPlatform.FcmV1 : NotificationPlatform.Apns,
            PushChannel = device.Token,
            Tags = [UserTag(device.UserId)]
        };
        await _client.CreateOrUpdateInstallationAsync(installation, ct);
    }

    public async Task UnregisterAsync(Device device, CancellationToken ct)
    {
        if (_client is null || device.Platform == DevicePlatform.Web)
        {
            return;
        }
        await _client.DeleteInstallationAsync(device.Id.ToString(), ct);
    }

    public async Task<SendResult> SendAsync(Delivery delivery, CancellationToken ct)
    {
        var item = delivery.InboxItem;
        if (_client is null)
        {
            logger.LogInformation("[push not configured] To {UserId}: {Title} — {Body}", delivery.UserId, item.Title, item.Body);
            return SendResult.Ok;
        }

        var platforms = await db.Devices.Where(d => d.UserId == delivery.UserId).Select(d => d.Platform).Distinct().ToListAsync(ct);
        var tag = UserTag(delivery.UserId);

        if (platforms.Contains(DevicePlatform.Android))
        {
            var fcm = JsonSerializer.Serialize(new
            {
                message = new
                {
                    notification = new { title = item.Title, body = item.Body },
                    data = new { link = item.Link ?? "" }
                }
            });
            await _client.SendFcmV1NativeNotificationAsync(fcm, tag, ct);
        }

        if (platforms.Contains(DevicePlatform.iOS))
        {
            var apns = JsonSerializer.Serialize(new
            {
                aps = new { alert = new { title = item.Title, body = item.Body }, sound = "default" },
                link = item.Link ?? ""
            });
            await _client.SendAppleNativeNotificationAsync(apns, tag, ct);
        }

        return SendResult.Ok;
    }

    private static NotificationHubClient? CreateClient(IConfiguration configuration)
    {
        var options = configuration.GetSection("NotificationHubs").Get<NotificationHubOptions>() ?? new NotificationHubOptions();
        return string.IsNullOrWhiteSpace(options.ConnectionString)
            ? null
            : NotificationHubClient.CreateClientFromConnectionString(options.ConnectionString, options.HubName);
    }

    private static string UserTag(string userId) => $"user:{userId}";
}

public sealed class WebPushOptions
{
    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }

    /// <summary>"mailto:" address or site URL identifying the sender to push services.</summary>
    public string Subject { get; set; } = "mailto:admin@tvtracker.local";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>Browser push (VAPID). Subscriptions the push service reports as gone are removed.</summary>
public sealed class WebPushSender(NotificationsContext db, IConfiguration configuration, ILogger<WebPushSender> logger) : IChannelSender
{
    private readonly WebPushOptions _options = configuration.GetSection("WebPush").Get<WebPushOptions>() ?? new WebPushOptions();

    public DeliveryChannel Channel => DeliveryChannel.WebPush;

    public async Task<SendResult> SendAsync(Delivery delivery, CancellationToken ct)
    {
        var item = delivery.InboxItem;
        if (!_options.IsConfigured)
        {
            logger.LogInformation("[web push not configured] To {UserId}: {Title} — {Body}", delivery.UserId, item.Title, item.Body);
            return SendResult.Ok;
        }

        var devices = await db.Devices.Where(d => d.UserId == delivery.UserId && d.Platform == DevicePlatform.Web).ToListAsync(ct);
        var payload = JsonSerializer.Serialize(new { title = item.Title, body = item.Body, link = item.Link });
        var vapid = new VapidDetails(_options.Subject, _options.PublicKey, _options.PrivateKey);
        var client = new WebPushClient();
        var sent = 0;

        foreach (var device in devices)
        {
            try
            {
                await client.SendNotificationAsync(new PushSubscription(device.Token, device.P256dh, device.Auth), payload, vapid);
                sent++;
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                logger.LogInformation("Removing expired web push subscription {DeviceId}", device.Id);
                db.Devices.Remove(device);
            }
            catch (WebPushException ex)
            {
                logger.LogWarning(ex, "Web push to {DeviceId} failed", device.Id);
            }
        }

        await db.SaveChangesAsync(ct);
        return sent > 0 || devices.Count == 0 ? SendResult.Ok : new SendResult(false, "No web push subscription accepted the message.");
    }
}

public sealed class EmailOptions
{
    public string? ConnectionString { get; set; }
    public string? SenderAddress { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString) && !string.IsNullOrWhiteSpace(SenderAddress);
}

/// <summary>Email through Azure Communication Services.</summary>
public sealed class EmailSender(NotificationsContext db, IConfiguration configuration, ILogger<EmailSender> logger) : IChannelSender
{
    private readonly EmailOptions _options = configuration.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();

    public DeliveryChannel Channel => DeliveryChannel.Email;

    public async Task<SendResult> SendAsync(Delivery delivery, CancellationToken ct)
    {
        var item = delivery.InboxItem;
        var email = await db.Preferences.Where(p => p.UserId == delivery.UserId).Select(p => p.Email).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(email))
        {
            return new SendResult(false, "No email address.");
        }

        if (!_options.IsConfigured)
        {
            logger.LogInformation("[email not configured] To {Email}: {Title} — {Body}", email, item.Title, item.Body);
            return SendResult.Ok;
        }

        var client = new EmailClient(_options.ConnectionString);
        var html = $"<p><strong>{WebUtility.HtmlEncode(item.Title)}</strong></p><p>{WebUtility.HtmlEncode(item.Body)}</p>";
        await client.SendAsync(WaitUntil.Started, _options.SenderAddress, email, item.Title, html, item.Body, ct);
        return SendResult.Ok;
    }
}
