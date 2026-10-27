using Microsoft.JSInterop;

namespace TvTracker.UI.Services;

/// <summary>
/// The colour theme: "light", "dark", or null to follow the device. A setting of the device or browser, like the
/// spoiler shield. theme.js applies it as data-theme on the page; app.css picks every colour with light-dark().
/// </summary>
public sealed class ThemeStore(IPlatformServices platform, IJSRuntime js)
{
    private const string Key = "theme";

    public static readonly IReadOnlyList<(string? Value, string Label)> Choices =
        [(null, "Same as device"), ("light", "Light"), ("dark", "Dark")];

    public async Task<string?> GetAsync()
    {
        try
        {
            return Normalize(await platform.GetPreferenceAsync(Key));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or JSException)
        {
            return null;
        }
    }

    public async Task SetAsync(string? theme)
    {
        theme = Normalize(theme);
        await platform.SetPreferenceAsync(Key, theme);
        await ApplyAsync(theme);
    }

    /// <summary>
    /// Applies the saved theme to the page. The web page's head script already has; the phone keeps the setting in
    /// its own preferences, which the page can't read before it starts.
    /// </summary>
    public async Task ApplySavedAsync() => await ApplyAsync(await GetAsync());

    private async Task ApplyAsync(string? theme)
    {
        try
        {
            await js.InvokeVoidAsync("tvtrackerTheme.apply", theme);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or JSException or JSDisconnectedException)
        {
            // A disconnected circuit: the next page load applies it.
        }
    }

    private static string? Normalize(string? theme) => theme is "light" or "dark" ? theme : null;
}
