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
    /// What the pages say about that source: the footer's credit, the terms, the privacy policy and people's pages
    /// all read it from here, so a new source is described in one place.
    /// </summary>
    public CatalogSourceInfo Catalog => CatalogSourceInfo.For(CatalogSource);

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

/// <summary>
/// A catalog source as the UI describes it.
/// </summary>
/// <param name="Name">As pages say it ("Details from TVmaze").</param>
/// <param name="Url">The source's site; null for the invented development catalog.</param>
/// <param name="ImagesFrom">Whose servers show pictures are loaded from, for the privacy policy.</param>
/// <param name="DataFrom">Everyone show data comes from, for the privacy policy.</param>
/// <param name="OwnLicence">The source words its own licence and credit (TheTVDB), where the others' data is CC BY-SA or freer.</param>
public sealed record CatalogSourceInfo(string Name, string? Url, string ImagesFrom, string DataFrom, bool OwnLicence = false)
{
    private static readonly CatalogSourceInfo TvMaze =
        new("TVmaze", "https://www.tvmaze.com", "TVmaze", "TVmaze, Wikidata and Wikiquote");

    private static readonly Dictionary<string, CatalogSourceInfo> Sources = new()
    {
        ["tvmaze"] = TvMaze,
        ["tvdb"] = new("TheTVDB", "https://thetvdb.com", "TheTVDB", "TheTVDB, Wikidata and Wikiquote", OwnLicence: true),
        ["wikidata"] = new("Wikidata and Wikipedia", "https://www.wikidata.org", "Wikimedia Commons", "Wikidata, Wikipedia and Wikiquote"),
        ["mock"] = new("our development catalog", null, "a placeholder image service", "an invented catalog, for development")
    };

    /// <summary>
    /// The source with that key (<c>Catalog:Provider</c>); TVmaze, the default provider, for one not known.
    /// </summary>
    public static CatalogSourceInfo For(string source) => Sources.GetValueOrDefault(source, TvMaze);
}
