using TvTracker.UI.Components;
using TvTracker.UI.Models;

namespace TvTracker.UI.Services;

/// <summary>
/// The app's name, for white-labelling: the web app passes <c>Branding:AppName</c> from its configuration to
/// <see cref="UiServiceCollectionExtensions.AddTvTrackerUi"/>, and the phone <c>MobileSettings.AppName</c>, since a MAUI
/// app has no configuration file. Pages inject it as <c>Brand</c>. An admin can set all of it in Settings instead
/// (<see cref="Apply"/>): what's set there wins, and what's left empty there keeps the configured value.
/// <para>
/// The privacy policy and terms also name who runs the app and how to reach them (<c>Branding:Operator</c>,
/// <c>Branding:ContactEmail</c>, and <c>Branding:OperatorAddress</c>, <c>CompanyNumber</c> and <c>GoverningLaw</c>),
/// since each white-labelled deployment has its own; without them the pages speak of the app by name and point to
/// Settings for the rights it serves there.
/// </para>
/// </summary>
public sealed class Branding(string? appName = null, string? operatorName = null, string? contactEmail = null, string? catalogSource = null,
    string? operatorAddress = null, string? companyNumber = null, string? governingLaw = null)
{
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // What the app was started with; Settings' values, when there are any, go on top.
    private readonly string _appName = Text(appName) ?? DefaultAppName;
    private readonly string? _operator = Text(operatorName);
    private readonly string? _contactEmail = Text(contactEmail);
    private readonly string? _operatorAddress = Text(operatorAddress);
    private readonly string? _companyNumber = Text(companyNumber);
    private readonly string? _governingLaw = Text(governingLaw);
    private volatile BrandingSettings _settings = BrandingSettings.None;

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

    public string AppName => Text(_settings.AppName) ?? _appName;

    /// <summary>
    /// The colour of links, buttons and the logo tile, as #rrggbb, when an admin set one; else the stylesheet's own.
    /// </summary>
    public string? AccentColor => Text(_settings.AccentColor);

    /// <summary>
    /// Who runs the app (a company's legal name), else the app's own name.
    /// </summary>
    public string Operator => Text(_settings.Operator) ?? _operator ?? AppName;

    /// <summary>
    /// Where to write about privacy and the terms; null when not set.
    /// </summary>
    public string? ContactEmail => Text(_settings.ContactEmail) ?? _contactEmail;

    /// <summary>
    /// The operator's postal address, which the law in most places wants a seller and a keeper of personal data to
    /// state; null when not set.
    /// </summary>
    public string? OperatorAddress => Text(_settings.OperatorAddress) ?? _operatorAddress;

    /// <summary>
    /// The operator's company registration number and where it's registered, as one phrase (e.g. "registered in
    /// South Africa, no. 2026/123456/07"); null when not set.
    /// </summary>
    public string? CompanyNumber => Text(_settings.CompanyNumber) ?? _companyNumber;

    /// <summary>
    /// Whose law the terms are under (e.g. "South Africa" or "England and Wales"); null when not set, and the terms
    /// then say the place the operator is established.
    /// </summary>
    public string? GoverningLaw => Text(_settings.GoverningLaw) ?? _governingLaw;

    /// <summary>
    /// When Settings' values were last read, for <see cref="BrandingLoader"/> to know when to ask again.
    /// </summary>
    public DateTimeOffset? LoadedAt { get; private set; }

    /// <summary>
    /// Raised when Settings' values change what the pages show.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Takes what an admin set in Settings; a field left empty there keeps the value the app was started with.
    /// </summary>
    public void Apply(BrandingSettings? settings, DateTimeOffset now)
    {
        var changed = (settings ?? BrandingSettings.None) != _settings;
        _settings = settings ?? BrandingSettings.None;
        LoadedAt = now;
        if (changed)
        {
            Changed?.Invoke();
        }
    }
}

/// <summary>
/// Reads the name, colour and company details an admin set in Settings (Identity.API keeps them) into
/// <see cref="Branding"/>: when the first page opens and then at most once a minute, so a change made by one admin
/// reaches everyone without a restart. A failure leaves what was there.
/// </summary>
public sealed class BrandingLoader(IdentityClient identity, Branding brand, TimeProvider time)
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    public async Task EnsureFreshAsync()
    {
        var now = time.GetUtcNow();
        if (brand.LoadedAt is { } loaded && now - loaded < Every)
        {
            return;
        }
        try
        {
            brand.Apply(await identity.GetBrandingAsync(), now);
        }
        catch (Exception ex) when (ex is HttpRequestException or SignInRequiredException or OperationCanceledException or System.Text.Json.JsonException)
        {
            // Identity.API is away: the name and details the app has stand.
        }
    }
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
