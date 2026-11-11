using TvTracker.UI.Components;

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
/// How a catalog source's data may be used, which decides the wording of its credit and of the terms.
/// </summary>
public enum CatalogLicence
{
    /// <summary>
    /// CC BY-SA, or freer with CC BY-SA text beside it (Wikidata with Wikipedia's overviews): credited with that licence.
    /// </summary>
    CcBySa,

    /// <summary>
    /// The source's own licence, with a credit it words itself (TheTVDB).
    /// </summary>
    Own,

    /// <summary>
    /// Invented data, for development: nothing to license or credit.
    /// </summary>
    Invented
}

/// <summary>
/// Someone credited for the catalog's data, with their site.
/// </summary>
public sealed record CatalogCredit(string Name, string Url);

/// <summary>
/// A catalog source as the UI describes it.
/// </summary>
/// <param name="Name">As pages say it when nobody is credited ("our development catalog"), and in sentences about the source.</param>
/// <param name="ImagesFrom">Whose servers show pictures are loaded from, for the privacy policy.</param>
/// <param name="Licence">What its data comes under.</param>
/// <param name="Credits">Who is credited for its data, the source first, then anyone whose text it brings with it (Wikipedia's overviews with Wikidata); none for invented data.</param>
public sealed record CatalogSourceInfo(string Name, string ImagesFrom, CatalogLicence Licence, IReadOnlyList<CatalogCredit> Credits)
{
    private static readonly CatalogSourceInfo TvMaze =
        new("TVmaze", "TVmaze", CatalogLicence.CcBySa, [new("TVmaze", "https://www.tvmaze.com")]);

    private static readonly Dictionary<string, CatalogSourceInfo> Sources = new()
    {
        ["tvmaze"] = TvMaze,
        ["tvdb"] = new("TheTVDB", "TheTVDB", CatalogLicence.Own, [new("TheTVDB", "https://thetvdb.com")]),
        ["wikidata"] = new("Wikidata", "Wikimedia Commons", CatalogLicence.CcBySa,
            [new("Wikidata", "https://www.wikidata.org"), new("Wikipedia", "https://en.wikipedia.org")]),
        ["mock"] = new("our development catalog", "a placeholder image service", CatalogLicence.Invented, [])
    };

    /// <summary>
    /// The source with that key (<c>Catalog:Provider</c>; the keys are Catalog.Data's CatalogSources, which the UI
    /// doesn't reference); TVmaze, the default provider, for one not known.
    /// </summary>
    public static CatalogSourceInfo For(string source) => Sources.GetValueOrDefault(source, TvMaze);

    /// <summary>
    /// Everyone show data comes from, for the privacy policy ("TVmaze, Wikidata and Wikiquote"): those credited (the
    /// source by name when nobody is), then the two every catalog takes quotes and facts from.
    /// </summary>
    public string DataFrom =>
        Format.List((Credits.Count > 0 ? Credits.Select(c => c.Name) : [Name]).Concat(["Wikidata", "Wikiquote"]).Distinct().ToList());
}
