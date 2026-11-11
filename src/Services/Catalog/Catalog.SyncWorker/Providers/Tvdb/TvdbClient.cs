using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Catalog.SyncWorker.Providers.Tvdb;

/// <summary>
/// Settings for TheTVDB (the <c>Tvdb</c> section), used when <c>Catalog:Provider</c> is "tvdb".
/// </summary>
public sealed class TvdbOptions
{
    public string BaseUrl { get; set; } = "https://api4.thetvdb.com/v4/";

    /// <summary>
    /// The project's v4 API key, from a TheTVDB account. Required; a secret, so it comes from the AppHost's
    /// <c>tvdb-api-key</c> parameter, not from appsettings.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// A subscriber's PIN, for a key on TheTVDB's user-supported model; not needed with a negotiated (licensed) key.
    /// </summary>
    public string? Pin { get; set; }

    /// <summary>
    /// Countries of origin whose best-scored shows are imported proactively, as TheTVDB's three-letter codes; "usa" and
    /// "gbr" when not set. No default here: configuration adds to an array that has one instead of replacing it, so
    /// the defaults could never be taken out.
    /// </summary>
    public string[]? FeaturedCountries { get; set; }

    /// <summary>
    /// <see cref="FeaturedCountries"/>, or the defaults.
    /// </summary>
    public IReadOnlyList<string> Countries => FeaturedCountries is { Length: > 0 } set ? set : ["usa", "gbr"];

    /// <summary>
    /// The language titles, overviews and episode names are wanted in, as TheTVDB's three-letter code: a show made in
    /// another language is read in this one where TheTVDB has the translation.
    /// </summary>
    public string Language { get; set; } = "eng";

    /// <summary>
    /// The original language of the featured shows, as TheTVDB's three-letter code. Not <see cref="Language"/>: reading
    /// titles in German shouldn't cut the featured US and UK shows down to those made in German.
    /// </summary>
    public string FeaturedLanguage { get; set; } = "eng";

    public int FeaturedCount { get; set; } = 40;
}

/// <summary>
/// TheTVDB's v4 API: signs in with the key for a bearer token (valid about a month), keeps it, and signs in again when
/// a request is refused. A singleton, so every caller shares the token; at most <see cref="MaxAtOnce"/> requests at a time.
/// </summary>
public sealed class TvdbClient(IHttpClientFactory httpFactory, IOptions<TvdbOptions> options, TimeProvider time)
{
    public const string HttpClientName = "tvdb";
    private const int MaxAtOnce = 4;

    /// <summary>
    /// How long a refused sign-in stands before the key is tried again: with a wrong or revoked key every background
    /// job would otherwise send TheTVDB a new sign-in for each request, for as long as the worker runs.
    /// </summary>
    public static readonly TimeSpan RefusedFor = TimeSpan.FromMinutes(5);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly SemaphoreSlim gate = new(MaxAtOnce);
    private readonly SemaphoreSlim signIn = new(1);
    private string? token;
    private DateTimeOffset refusedUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// The response to a GET of <paramref name="path"/> (relative to the API's base), or null when TheTVDB has no such
    /// record (404). Any other failure throws <see cref="HttpRequestException"/>.
    /// </summary>
    public async Task<TvdbEnvelope<T>?> GetAsync<T>(string path, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            var bearer = token ?? await SignInAsync(http, null, ct);
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                using var response = await http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    // The token expired or was revoked: one new sign-in, then the request again.
                    bearer = await SignInAsync(http, bearer, ct);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                response.EnsureSuccessStatusCode();
                try
                {
                    return await response.Content.ReadFromJsonAsync<TvdbEnvelope<T>>(Json, ct);
                }
                catch (JsonException ex)
                {
                    // As a failed request, which every caller already handles one show at a time; a JsonException
                    // would pass their catches and end the whole round.
                    throw new HttpRequestException($"TheTVDB answered {path} in a form that couldn't be read: {ex.Message}", ex);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A paged listing read to its end: pages from 0 until one is empty or names no next page, at most
    /// <paramref name="maxPages"/>. Complete is false when it stopped at that limit with more to come.
    /// </summary>
    public async Task<(List<TItem> Items, bool Complete)> PagesAsync<TPage, TItem>(Func<int, string> path, Func<TPage, IEnumerable<TItem>> items,
        int maxPages, CancellationToken ct) where TPage : class
    {
        var all = new List<TItem>();
        for (var page = 0; page < maxPages; page++)
        {
            var found = await GetAsync<TPage>(path(page), ct);
            var read = found?.Data is { } data ? items(data).ToList() : [];
            all.AddRange(read);
            if (read.Count == 0 || found?.Links?.Next is null)
            {
                return (all, true);
            }
        }
        return (all, false);
    }

    private static HttpRequestException Refused() =>
        new("TheTVDB refused the API key (Tvdb:ApiKey, and Tvdb:Pin if the key needs one).", null, HttpStatusCode.Unauthorized);

    /// <summary>
    /// A token other than <paramref name="stale"/>: the one another caller just got, or a new one.
    /// </summary>
    private async Task<string> SignInAsync(HttpClient http, string? stale, CancellationToken ct)
    {
        await signIn.WaitAsync(ct);
        try
        {
            if (token is { } current && current != stale)
            {
                return current;
            }
            if (time.GetUtcNow() < refusedUntil)
            {
                throw Refused();
            }
            var body = new Dictionary<string, string> { ["apikey"] = options.Value.ApiKey ?? "" };
            if (!string.IsNullOrWhiteSpace(options.Value.Pin))
            {
                body["pin"] = options.Value.Pin;
            }
            using var response = await http.PostAsJsonAsync("login", body, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                token = null;
                refusedUntil = time.GetUtcNow() + RefusedFor;
                throw Refused();
            }
            response.EnsureSuccessStatusCode();
            var signedIn = await response.Content.ReadFromJsonAsync<TvdbEnvelope<TvdbLogin>>(Json, ct);
            token = signedIn?.Data?.Token is { Length: > 0 } found
                ? found
                : throw new HttpRequestException("TheTVDB's sign-in gave no token.");
            return token;
        }
        finally
        {
            signIn.Release();
        }
    }
}

// TheTVDB's records, with only the fields the catalog reads (https://thetvdb.github.io/v4-api/).

public sealed record TvdbEnvelope<T>(string? Status, T? Data, TvdbLinks? Links);

/// <param name="Next">The next page's address; null on the last page.</param>
public sealed record TvdbLinks(string? Next);

public sealed record TvdbLogin(string? Token);

/// <param name="Name">"Continuing", "Ended" or "Upcoming".</param>
public sealed record TvdbStatus(string? Name);

public sealed record TvdbCompany(string? Name, string? Country);

public sealed record TvdbGenre(string? Name);

/// <param name="Type">TheTVDB's artwork type: 1 banner, 2 poster, 3 background (for a series).</param>
public sealed record TvdbArtwork(string? Image, int? Type, double? Score, string? Language);

/// <param name="Type">"official" (aired order), "dvd", "absolute", …</param>
public sealed record TvdbSeasonType(string? Type);

public sealed record TvdbSeason(int? Number, string? Name, TvdbSeasonType? Type);

/// <summary>
/// The series a person's credit is on, as their record carries it.
/// </summary>
public sealed record TvdbRecordInfo(string? Name, string? Image);

/// <summary>
/// A credit: a character an actor plays, or a crew role (then <paramref name="Name"/> is usually empty).
/// </summary>
/// <param name="PeopleType">"Actor", "Guest Star", "Creator", "Executive Producer", "Director", "Writer", …</param>
public sealed record TvdbCharacter(long Id, string? Name, long? PeopleId, string? PersonName, string? PersonImgURL, string? Image,
    string? PeopleType, long? SeriesId, TvdbRecordInfo? Series, int? Sort, bool? IsFeatured);

/// <param name="Aired">The day it first aired ("2008-01-20"); TheTVDB has no time of day per episode.</param>
public sealed record TvdbEpisode(long Id, string? Name, string? Overview, string? Aired, int? Runtime, int? Number, int? SeasonNumber);

/// <summary>
/// A page of a series' episodes. The translated listing carries them inside the series record; either place is read.
/// </summary>
public sealed record TvdbEpisodePage(List<TvdbEpisode>? Episodes, TvdbSeries? Series)
{
    public List<TvdbEpisode> All => Episodes ?? Series?.Episodes ?? [];
}

/// <summary>
/// A name or an overview in one language.
/// </summary>
public sealed record TvdbTranslation(string? Language, string? Name, string? Overview);

/// <summary>
/// A record's translations, with <c>meta=translations</c>.
/// </summary>
public sealed record TvdbTranslations(List<TvdbTranslation>? NameTranslations, List<TvdbTranslation>? OverviewTranslations);

public sealed record TvdbSeries(long Id, string? Name, string? Overview, string? Image, string? FirstAired, string? LastAired,
    TvdbStatus? Status, string? OriginalCountry, string? OriginalLanguage, double? Score, TvdbCompany? OriginalNetwork,
    TvdbCompany? LatestNetwork, List<TvdbGenre>? Genres, List<TvdbArtwork>? Artworks, List<TvdbSeason>? Seasons,
    List<TvdbCharacter>? Characters, string? AirsTime = null, TvdbTranslations? Translations = null, List<TvdbEpisode>? Episodes = null);

/// <summary>
/// A search hit; search has its own field names.
/// </summary>
public sealed record TvdbSearchResult(
    [property: JsonPropertyName("tvdb_id")] string? TvdbId,
    string? Name,
    [property: JsonPropertyName("image_url")] string? ImageUrl,
    [property: JsonPropertyName("first_air_time")] string? FirstAirTime,
    string? Network,
    string? Country,
    [property: JsonPropertyName("primary_language")] string? PrimaryLanguage,
    string? Status,
    string? Type,
    Dictionary<string, string>? Translations = null);

/// <summary>
/// What a remote id (IMDb's, …) belongs to: one of a series, a movie, a person, an episode or a company.
/// </summary>
public sealed record TvdbRemoteMatch(TvdbSeries? Series);

/// <param name="RecordType">"series", "episodes", "people", …</param>
/// <param name="TimeStamp">When it changed, in Unix seconds.</param>
/// <param name="SeriesId">The episode's series; only on episode records.</param>
/// <param name="MethodInt">1 created, 2 updated, 3 deleted.</param>
public sealed record TvdbUpdate(string? RecordType, long RecordId, long TimeStamp, long? SeriesId, int? MethodInt = null)
{
    public const int Deleted = 3;
}

public sealed record TvdbPerson(long Id, string? Name, List<TvdbCharacter>? Characters);
