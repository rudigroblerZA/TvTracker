namespace TvTracker.UI.Services;

// Services the shared UI needs from its host. The web app and the mobile app each implement them.

/// <summary>Supplies the signed-in user's access token for API calls (null when signed out).</summary>
public interface IAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Starts sign-in and sign-out (Keycloak handles sign-up, password reset and external logins).</summary>
public interface IAuthActions
{
    Task SignInAsync(string? returnUrl = null);

    Task SignOutAsync();
}

/// <summary>Platform features that differ between the browser and the phone.</summary>
public interface IPlatformServices
{
    /// <summary>IANA timezone of this device/browser, used to suggest the profile timezone.</summary>
    Task<string?> GetDeviceTimeZoneAsync();

    /// <summary>Whether this host can receive push notifications.</summary>
    bool SupportsPush { get; }

    /// <summary>Asks for permission and registers this device or browser for push. Returns true on success.</summary>
    Task<bool> EnablePushAsync();

    /// <summary>Saves or shares a text file (used for the data export).</summary>
    Task SaveTextFileAsync(string fileName, string content);

    /// <summary>A setting kept on this device or browser (the spoiler shield); null when never set.</summary>
    Task<string?> GetPreferenceAsync(string key);

    /// <summary>Keeps a setting on this device or browser; null removes it.</summary>
    Task SetPreferenceAsync(string key, string? value);

    /// <summary>
    /// The web app's address, ending in "/", for links people open elsewhere (a list's link): the browser's own on the
    /// web, the configured one on the phone.
    /// </summary>
    string PublicWebAddress { get; }

    /// <summary>Shares a link: the share sheet on a phone, else copies it. True when it was shared or copied.</summary>
    Task<bool> ShareLinkAsync(string title, string url);
}

/// <summary>Offline support. The web app uses <see cref="OnlineOnly"/>; the mobile app stores responses and queues changes in SQLite.</summary>
public interface IOfflineStore
{
    bool IsEnabled { get; }

    Task StoreResponseAsync(string key, string json);

    Task<string?> GetResponseAsync(string key);

    /// <summary>Queues a change made while offline, to be sent when the connection is back.</summary>
    Task EnqueueAsync(PendingRequest request);
}

public record PendingRequest(string Client, string Method, string Url, string? Body, DateTimeOffset CreatedAtUtc);

public sealed class OnlineOnly : IOfflineStore
{
    public bool IsEnabled => false;
    public Task StoreResponseAsync(string key, string json) => Task.CompletedTask;
    public Task<string?> GetResponseAsync(string key) => Task.FromResult<string?>(null);
    public Task EnqueueAsync(PendingRequest request) => throw new NotSupportedException("Offline changes are not supported here.");
}
