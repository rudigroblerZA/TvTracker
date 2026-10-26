using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using TvTracker.UI.Models;
using TvTracker.UI.Services;

namespace WebApp.Services;

/// <summary>Reads the signed-in user's access token from the server-side token store.</summary>
public sealed class WebAccessTokenProvider(AuthenticationStateProvider authState, ServerTokenStore tokens) : IAccessTokenProvider
{
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var state = await authState.GetAuthenticationStateAsync();
        var userId = state.User.FindFirst("sub")?.Value;
        return userId is null ? null : await tokens.GetValidAccessTokenAsync(userId, cancellationToken);
    }
}

/// <summary>Sign-in and sign-out are full-page redirects through the account endpoints (see Program.cs).</summary>
public sealed class WebAuthActions(NavigationManager navigation) : IAuthActions
{
    public Task SignInAsync(string? returnUrl = null)
    {
        var target = navigation.ToBaseRelativePath(returnUrl ?? navigation.Uri);
        navigation.NavigateTo($"account/login?returnUrl={Uri.EscapeDataString("/" + target)}", forceLoad: true);
        return Task.CompletedTask;
    }

    public Task SignOutAsync()
    {
        navigation.NavigateTo("account/logout", forceLoad: true);
        return Task.CompletedTask;
    }
}

/// <summary>Browser features through wwwroot/js/tvtracker.js.</summary>
public sealed class WebPlatformServices(IJSRuntime js, NotificationsClient notifications) : IPlatformServices
{
    public bool SupportsPush => true;

    public async Task<string?> GetDeviceTimeZoneAsync() => await js.InvokeAsync<string?>("tvtracker.timeZone");

    public async Task<bool> EnablePushAsync()
    {
        var publicKey = await notifications.GetWebPushPublicKeyAsync();
        if (string.IsNullOrEmpty(publicKey))
        {
            return false;
        }

        var subscription = await js.InvokeAsync<BrowserPushSubscription?>("tvtracker.subscribePush", publicKey);
        if (subscription is null)
        {
            return false;
        }

        var device = await notifications.RegisterDeviceAsync(
            new RegisterDevice(DevicePlatform.Web, subscription.Endpoint, subscription.P256dh, subscription.Auth));
        return device is not null;
    }

    public async Task SaveTextFileAsync(string fileName, string content) =>
        await js.InvokeVoidAsync("tvtracker.downloadText", fileName, content);

    public async Task<string?> GetPreferenceAsync(string key) => await js.InvokeAsync<string?>("tvtracker.getPreference", key);

    public async Task SetPreferenceAsync(string key, string? value) => await js.InvokeVoidAsync("tvtracker.setPreference", key, value);

    private sealed record BrowserPushSubscription(string Endpoint, string P256dh, string Auth);
}
