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
public sealed class Branding(string? appName = null, string? operatorName = null, string? contactEmail = null, string? catalogSource = null)
{
    /// <summary>
    /// Where the catalog's data comes from (<c>Catalog:Provider</c>: "tvmaze", "wikidata", "tvdb" or "mock"), for the
    /// credit in the footer that TVmaze's and TheTVDB's licences require. TVmaze when not said.
    /// </summary>
    public string CatalogSource { get; } = string.IsNullOrWhiteSpace(catalogSource) ? "tvmaze" : catalogSource.Trim().ToLowerInvariant();

    /// <summary>
    /// The source's name as pages say it ("Details from TVmaze"), so the terms, the privacy policy and people's pages
    /// name the source the catalog really has.
    /// </summary>
    public string CatalogSourceName => CatalogSource switch
    {
        "tvdb" => "TheTVDB",
        "wikidata" => "Wikidata",
        "mock" => "our development catalog",
        _ => "TVmaze"
    };

    /// <summary>
    /// The source's site; null for the invented development catalog.
    /// </summary>
    public string? CatalogSourceUrl => CatalogSource switch
    {
        "tvdb" => "https://thetvdb.com",
        "wikidata" => "https://www.wikidata.org",
        "mock" => null,
        _ => "https://www.tvmaze.com"
    };

    /// <summary>
    /// Whose servers show pictures are loaded from, for the privacy policy.
    /// </summary>
    public string CatalogImagesFrom => CatalogSource switch
    {
        "tvdb" => "TheTVDB",
        "wikidata" => "Wikimedia Commons",
        "mock" => "a placeholder image service",
        _ => "TVmaze"
    };

    /// <summary>
    /// The licence the source's data comes under, as the terms state it.
    /// </summary>
    public string CatalogLicence => CatalogSource switch
    {
        "tvdb" => "TheTVDB's licence",
        "mock" => "no licence (it's invented)",
        _ => "the CC BY-SA licence"
    };

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
