namespace TvTracker.UI.Services;

/// <summary>
/// The spoiler shield: when on, titles, summaries and stills of episodes the user hasn't watched are hidden until
/// tapped. A setting of the device or browser rather than the profile, the way a "hide spoilers" switch is used: on the
/// shared TV-room tablet, not everywhere. It also needs no schema change in Identity, which still uses EnsureCreated.
/// </summary>
public sealed class SpoilerShield(IPlatformServices platform, FeatureFlagStore flags)
{
    private const string Key = "spoiler-shield";

    private Task<bool>? loading;

    /// <summary>Raised when the setting changes, so hidden content shows or hides everywhere on the page.</summary>
    public event Action? Changed;

    /// <summary>Whether the shield is on; false while the feature flag is off. Loaded once per circuit or app session.</summary>
    public Task<bool> IsOnAsync() => loading ??= LoadAsync();

    public async Task SetAsync(bool on)
    {
        await platform.SetPreferenceAsync(Key, on ? "on" : null);
        loading = Task.FromResult(on && await flags.IsEnabledAsync(FeatureFlags.SpoilerShield));
        Changed?.Invoke();
    }

    private async Task<bool> LoadAsync()
    {
        try
        {
            return await platform.GetPreferenceAsync(Key) == "on" && await flags.IsEnabledAsync(FeatureFlags.SpoilerShield);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or Microsoft.JSInterop.JSException)
        {
            // Storage unavailable (a disconnected circuit, blocked storage): shield off, as if never set.
            loading = null;
            return false;
        }
    }
}
