using TvTracker.UI.Models;
using TvTracker.UI.Services;
using UiDevicePlatform = TvTracker.UI.Models.DevicePlatform;

namespace TvTracker.Mobile.Services;

/// <summary>
/// Supplies the platform push token (FCM on Android, APNs on iOS).
/// Wiring this up needs a Firebase project (google-services.json) and an Apple push certificate,
/// e.g. with the Plugin.Firebase or Shiny.Push packages; until then push is unavailable on mobile.
/// </summary>
public interface IPushTokenProvider
{
    bool IsConfigured { get; }

    Task<string?> GetTokenAsync();
}

public sealed class NotConfiguredPushTokenProvider : IPushTokenProvider
{
    public bool IsConfigured => false;

    public Task<string?> GetTokenAsync() => Task.FromResult<string?>(null);
}

public sealed class MobilePlatformServices(IPushTokenProvider pushTokens, NotificationsClient notifications) : IPlatformServices
{
    public Task<string?> GetDeviceTimeZoneAsync()
    {
        var local = TimeZoneInfo.Local;
        // .NET on Android/iOS reports IANA ids; on Windows convert to IANA.
        var id = local.HasIanaId ? local.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : null;
        return Task.FromResult(id);
    }

    public Task<string?> GetPreferenceAsync(string key) =>
        Task.FromResult(Preferences.Default.ContainsKey(key) ? Preferences.Default.Get<string?>(key, null) : null);

    public Task SetPreferenceAsync(string key, string? value)
    {
        if (value is null)
        {
            Preferences.Default.Remove(key);
        }
        else
        {
            Preferences.Default.Set(key, value);
        }
        return Task.CompletedTask;
    }

    public bool SupportsPush => pushTokens.IsConfigured;

    public async Task<bool> EnablePushAsync()
    {
        var status = await Permissions.RequestAsync<Permissions.PostNotifications>();
        if (status != PermissionStatus.Granted)
        {
            return false;
        }

        var token = await pushTokens.GetTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var platform = DeviceInfo.Platform == Microsoft.Maui.Devices.DevicePlatform.Android
            ? UiDevicePlatform.Android
            : UiDevicePlatform.iOS;

        return await notifications.RegisterDeviceAsync(new RegisterDevice(platform, token, null, null)) is not null;
    }

    public async Task SaveTextFileAsync(string fileName, string content)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllTextAsync(path, content);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = $"{MobileSettings.AppName} data export",
            File = new ShareFile(path, "application/json")
        });
    }
}

/// <summary>Turns tvtracker:// links into in-app navigation (handled by MainPage).</summary>
public sealed class DeepLinkService
{
    private string? _pending;

    public event Action<string>? LinkReceived;

    /// <summary>tvtracker://shows/{id} → "shows/{id}". Sign-in callbacks are handled by WebAuthenticator.</summary>
    public void Handle(Uri uri)
    {
        if (!string.Equals(uri.Scheme, "tvtracker", StringComparison.OrdinalIgnoreCase) || uri.Host == "callback")
        {
            return;
        }

        var path = $"{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
        if (LinkReceived is null)
        {
            _pending = path;
        }
        else
        {
            LinkReceived(path);
        }
    }

    /// <summary>A link that arrived before the UI was ready (app opened from a link).</summary>
    public string? TakePending()
    {
        var path = _pending;
        _pending = null;
        return path;
    }
}
