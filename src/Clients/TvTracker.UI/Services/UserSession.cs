using Microsoft.AspNetCore.Components.Authorization;
using TvTracker.UI.Models;

namespace TvTracker.UI.Services;

/// <summary>
/// The signed-in user's profile for the current session: timezone for showing air times,
/// region for where-to-watch. Signed-out users get UTC and "US".
/// </summary>
public sealed class UserSession(IdentityClient identity, AuthenticationStateProvider authState)
{
    private Task? _loading;

    public Profile? Profile { get; private set; }

    public bool IsSignedIn { get; private set; }

    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Utc;

    public string Region => Profile?.Region ?? "US";

    public event Action? Changed;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public void SetProfile(Profile profile)
    {
        Profile = profile;
        TimeZone = TimeZoneInfo.TryFindSystemTimeZoneById(profile.TimeZone, out var tz) ? tz : TimeZoneInfo.Utc;
        Changed?.Invoke();
    }

    public DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, TimeZone);

    public DateTimeOffset Now => ToLocal(DateTimeOffset.UtcNow);

    private async Task LoadAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        IsSignedIn = state.User.Identity?.IsAuthenticated == true;
        if (!IsSignedIn)
        {
            return;
        }

        try
        {
            if (await identity.GetProfileAsync() is { } profile)
            {
                SetProfile(profile);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or SignInRequiredException)
        {
            // Keep defaults; pages show their own errors.
        }
    }
}
