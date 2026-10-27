namespace TvTracker;

/// <summary>
/// The app's name, for white-labelling, from <c>Branding:AppName</c> (the AppHost passes its own to the web app and the
/// services that put the name in text). The UI has its own <c>TvTracker.UI.Services.Branding</c>, since the phone
/// doesn't reference ServiceDefaults.
/// </summary>
public sealed class Branding
{
    public const string Section = "Branding";
    public const string DefaultAppName = "TV Tracker";

    public string AppName { get; }

    public Branding(string? appName = null) =>
        AppName = string.IsNullOrWhiteSpace(appName) ? DefaultAppName : appName.Trim();
}
