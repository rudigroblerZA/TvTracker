using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Catalog.Worker.JustWatch;

/// <summary>
/// JustWatch's Partner API (<c>JustWatch</c>): where a show streams, country by country. It is contract-only: the
/// token comes from JustWatch once an agreement is signed, and without one the whole feature is off.
/// </summary>
public sealed class JustWatchOptions
{
    /// <summary>
    /// The partner token. Empty turns JustWatch off.
    /// </summary>
    public string? Token { get; set; }

    public string BaseUrl { get; set; } = "https://apis.justwatch.com/contentpartner/";

    /// <summary>
    /// The countries asked about (ISO 3166-1); empty for the catalog's own (<c>TvMaze:WatchRegions</c>).
    /// </summary>
    public List<string> Regions { get; set; } = [];

    /// <summary>
    /// How long an answer is kept before the show is asked about again.
    /// </summary>
    public int RefreshDays { get; set; } = 7;

    /// <summary>
    /// Requests a minute, at most. JustWatch publishes no limit, so this is ours to keep.
    /// </summary>
    public int RequestsPerMinute { get; set; } = 60;
}

/// <summary>
/// JustWatch refused the token (401 or 403): nothing will work until it's put right.
/// </summary>
public sealed class JustWatchRefusedException(HttpStatusCode status) : Exception($"JustWatch refused the partner token ({(int)status}).");

/// <summary>
/// The Partner API's calls this app makes (v2 content routes of JustWatch's published OpenAPI document). The token
/// goes in the query string, as the API asks, so this client's requests are never logged.
/// </summary>
public sealed class JustWatchClient(HttpClient http, IOptions<JustWatchOptions> options)
{
    public const string HttpClientName = "justwatch";

    private string Token => Uri.EscapeDataString(options.Value.Token ?? "");

    public async Task<IReadOnlyList<JustWatchCountry>> CountriesAsync(CancellationToken ct) =>
        await GetAsync<List<JustWatchCountry>>($"v2/content/countries?token={Token}", ct) ?? [];

    public async Task<IReadOnlyList<JustWatchProvider>> ProvidersAsync(string locale, CancellationToken ct) =>
        await GetAsync<List<JustWatchProvider>>($"v2/content/providers/all/locale/{locale}?token={Token}", ct) ?? [];

    /// <summary>
    /// A show's offers in a locale's country, by IMDb id; null when JustWatch doesn't know the show.
    /// </summary>
    public Task<JustWatchTitle?> ShowByImdbAsync(string locale, string imdbId, CancellationToken ct) =>
        GetAsync<JustWatchTitle>($"v2/content/offers/object_type/show/id_type/imdb/locale/{locale}?id={Uri.EscapeDataString(imdbId)}&token={Token}", ct);

    /// <summary>
    /// A show's offers by title and the year it premiered, for shows without an IMDb id; null when there's no match.
    /// </summary>
    public Task<JustWatchTitle?> ShowByTitleAsync(string locale, string title, int year, CancellationToken ct) =>
        GetAsync<JustWatchTitle>(
            $"v2/content/offers/object_type/show/locale/{locale}?title={Uri.EscapeDataString(title)}&release_year={year}&token={Token}", ct);

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new JustWatchRefusedException(response.StatusCode);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }
}

/// <param name="FullLocale">Language and country ("en_US"): what offers are asked for by.</param>
public sealed record JustWatchCountry(
    [property: JsonPropertyName("full_locale")] string? FullLocale,
    [property: JsonPropertyName("status")] string? Status);

public sealed record JustWatchProvider(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("clear_name")] string? ClearName);

/// <param name="FullPath">The title's page on justwatch.com, from the site's root ("/us/tv-show/breaking-bad").</param>
public sealed record JustWatchTitle(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("original_title")] string? OriginalTitle,
    [property: JsonPropertyName("original_release_year")] int? OriginalReleaseYear,
    [property: JsonPropertyName("full_path")] string? FullPath,
    [property: JsonPropertyName("offers")] List<JustWatchOffer>? Offers);

/// <param name="MonetizationType">"flatrate", "free", "rent" or "buy" (and "ads" on some services).</param>
public sealed record JustWatchOffer(
    [property: JsonPropertyName("monetization_type")] string? MonetizationType,
    [property: JsonPropertyName("provider_id")] long ProviderId,
    [property: JsonPropertyName("urls")] JustWatchUrls? Urls);

/// <param name="StandardWeb">JustWatch's attributed link to the offer: the one its linking rules ask partners to use.</param>
/// <param name="RawWeb">The service's own address for the offer, when given; used only to recognise the service.</param>
public sealed record JustWatchUrls(
    [property: JsonPropertyName("standard_web")] string? StandardWeb,
    [property: JsonPropertyName("raw_web")] string? RawWeb);
