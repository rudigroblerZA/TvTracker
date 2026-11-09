namespace TvTracker.UI.Services;

/// <summary>
/// The app's name, for white-labelling: the web app passes <c>Branding:AppName</c> from its configuration to
/// <see cref="UiServiceCollectionExtensions.AddTvTrackerUi"/>, and the phone <c>MobileSettings.AppName</c>, since a MAUI
/// app has no configuration file. Pages inject it as <c>Brand</c>.
/// <para>
/// The privacy policy and terms also name who runs the app and how to reach them (<c>Branding:Operator</c>,
/// <c>Branding:ContactEmail</c>), since each white-labelled deployment has its own; without them the pages speak of the
/// app by name and point to Settings for the rights it serves there.
/// </para>
/// </summary>
public sealed class Branding(string? appName = null, string? operatorName = null, string? contactEmail = null)
{
    public const string DefaultAppName = "TV Tracker";

    public string AppName { get; } = string.IsNullOrWhiteSpace(appName) ? DefaultAppName : appName.Trim();

    /// <summary>
    /// Who runs the app (a company's legal name), else the app's own name.
    /// </summary>
    public string Operator { get; } = string.IsNullOrWhiteSpace(operatorName)
        ? (string.IsNullOrWhiteSpace(appName) ? DefaultAppName : appName.Trim())
        : operatorName.Trim();

    /// <summary>
    /// Where to write about privacy and the terms; null when not configured.
    /// </summary>
    public string? ContactEmail { get; } = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim();
}
