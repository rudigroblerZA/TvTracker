using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Research.API.Data;

namespace Research.API.Services;

public static class ExternalSources
{
    /// <summary>
    /// A client for outside sources, without the standard resilience handler: a refusal (the MediaWiki API answers
    /// 429 to shared addresses) has a fallback, while the handler's backoff on 429 used up the request's 30 s. Wikimedia
    /// asks API clients to identify themselves with a contact.
    /// </summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "TvTracker-research/1.0 (+https://github.com/rudigroblerZA/TvTracker)" } }
    };
}

/// <summary>What one source said: its text, and the facts it offers.</summary>
public sealed record FoundSource(string Kind, string Url, string Title, string Excerpt, IReadOnlyList<KeyValuePair<string, string>> Facts);

/// <summary>
/// Wikidata and Wikipedia. The show is found on Wikidata by its TVmaze id (property P8600), which also gives its
/// Wikipedia article and IMDb and TMDB ids, so nothing is matched by title when TVmaze is the provider.
/// <list type="bullet">
/// <item><b>Query service:</b> one SPARQL query fetches every fact. Wikidata's entity API rate-limits shared addresses
/// (it answered 429 from the development sandbox) where the query service didn't.</item>
/// <item><b>Wikipedia:</b> the article's whole introduction from the MediaWiki API, else (when that refuses) the REST
/// summary, a paragraph. Without a Wikidata item, likely titles ("X (TV series)") are tried, and a page is kept only
/// when Wikipedia describes it as a TV series.</item>
/// </list>
/// </summary>
public sealed partial class WikimediaSource(ILogger<WikimediaSource> logger)
{
    private static readonly HttpClient Http = ExternalSources.CreateClient();

    private const string Sparql = """
        SELECT ?item ?article ?imdb ?tmdb ?start ?end ?seasons ?episodes
          (GROUP_CONCAT(DISTINCT ?creatorLabel; separator="|") AS ?creators)
          (GROUP_CONCAT(DISTINCT ?networkLabel; separator="|") AS ?networks)
          (GROUP_CONCAT(DISTINCT ?awardLabel; separator="|") AS ?awards)
          (GROUP_CONCAT(DISTINCT ?countryLabel; separator="|") AS ?countries)
        WHERE {
          ?item wdt:P8600 "{ID}".
          OPTIONAL { ?article schema:about ?item; schema:isPartOf <https://en.wikipedia.org/>. }
          OPTIONAL { ?item wdt:P345 ?imdb. }
          OPTIONAL { ?item wdt:P4983 ?tmdb. }
          OPTIONAL { ?item wdt:P580 ?start. }
          OPTIONAL { ?item wdt:P582 ?end. }
          OPTIONAL { ?item wdt:P2437 ?seasons. }
          OPTIONAL { ?item wdt:P1113 ?episodes. }
          OPTIONAL { ?item wdt:P170 ?creator. ?creator rdfs:label ?creatorLabel. FILTER(LANG(?creatorLabel) = "en") }
          OPTIONAL { ?item wdt:P449 ?network. ?network rdfs:label ?networkLabel. FILTER(LANG(?networkLabel) = "en") }
          OPTIONAL { ?item wdt:P166 ?award. ?award rdfs:label ?awardLabel. FILTER(LANG(?awardLabel) = "en") }
          OPTIONAL { ?item wdt:P495 ?country. ?country rdfs:label ?countryLabel. FILTER(LANG(?countryLabel) = "en") }
        }
        GROUP BY ?item ?article ?imdb ?tmdb ?start ?end ?seasons ?episodes
        """;

    public async Task<(List<FoundSource> Found, List<string> Notes)> GatherAsync(CatalogShow show, CancellationToken ct)
    {
        var found = new List<FoundSource>();
        var notes = new List<string>();
        string? article = null;

        if (show.Source == "tvmaze" && show.ExternalId is { Length: > 0 } tvmazeId && int.TryParse(tvmazeId, out _))
        {
            try
            {
                if (await WikidataAsync(tvmazeId, ct) is { } wikidata)
                {
                    found.Add(wikidata);
                    article = wikidata.Facts.FirstOrDefault(f => f.Key == "wikipedia").Value;
                }
                else
                {
                    notes.Add($"Wikidata has no item with TVmaze id {tvmazeId}.");
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Wikidata lookup failed for {Show}", show.Title);
                notes.Add($"Wikidata didn't answer ({ex.Message}).");
            }
        }

        try
        {
            if (await WikipediaAsync(article, show.Title, ct) is { } wikipedia)
            {
                found.Add(wikipedia);
            }
            else
            {
                notes.Add("No Wikipedia article found; add its address by hand if there is one.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Wikipedia lookup failed for {Show}", show.Title);
            notes.Add($"Wikipedia didn't answer ({ex.Message}).");
        }

        return (found, notes);
    }

    private async Task<FoundSource?> WikidataAsync(string tvmazeId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://query.wikidata.org/sparql")
        {
            Content = new FormUrlEncodedContent([new("query", Sparql.Replace("{ID}", tvmazeId))])
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var row = json.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray().FirstOrDefault();
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? Get(string name) => row.TryGetProperty(name, out var v) && v.GetProperty("value").GetString() is { Length: > 0 } s ? s : null;
        IEnumerable<string> List(string name) => (Get(name) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var itemUrl = Get("item")!.Replace("http://www.wikidata.org/entity/", "https://www.wikidata.org/wiki/");
        var facts = new List<KeyValuePair<string, string>> { new("wikidata", itemUrl) };
        void Add(string key, string? value) { if (value is { Length: > 0 }) facts.Add(new(key, value)); }

        Add("wikipedia", Get("article"));
        Add("imdb", Get("imdb") is { } imdb ? $"https://www.imdb.com/title/{imdb}/" : null);
        Add("tmdb", Get("tmdb") is { } tmdb ? $"https://www.themoviedb.org/tv/{tmdb}" : null);
        Add("premiered", Date(Get("start")));
        Add("ended", Date(Get("end")));
        Add("seasons", Get("seasons"));
        Add("episodes", Get("episodes"));
        Add("creators", string.Join(", ", List("creators")));
        foreach (var network in List("networks"))
        {
            Add("network", network);
        }
        Add("country", string.Join(", ", List("countries")));
        Add("awards", Awards(List("awards")));

        var excerpt = string.Join("\n", facts.Select(f => $"{ResearchFacts.ByKey[f.Key].Label}: {f.Value}"));
        return new FoundSource(SourceKinds.Wikidata, itemUrl, $"Wikidata {itemUrl[(itemUrl.LastIndexOf('/') + 1)..]}", excerpt, facts);
    }

    /// <summary>
    /// Awards grouped by name, most won first: "Primetime Emmy Award × 6, Satellite Award × 3". Wikidata lists one item per
    /// category, and Breaking Bad's 30 categories read as a wall of text.
    /// </summary>
    internal static string Awards(IEnumerable<string> categories)
    {
        var groups = categories
            .Select(c => c.Split(" for ", 2)[0].Trim())
            .GroupBy(name => name)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Count() > 1 ? $"{g.Key} × {g.Count()}" : g.Key)
            .ToList();
        return groups.Count > 8 ? $"{string.Join(", ", groups.Take(8))} and {groups.Count - 8} more" : string.Join(", ", groups);
    }

    private async Task<FoundSource?> WikipediaAsync(string? articleUrl, string showTitle, CancellationToken ct)
    {
        var titles = articleUrl is not null
            ? [Uri.UnescapeDataString(articleUrl[(articleUrl.LastIndexOf('/') + 1)..])]
            : new[] { $"{showTitle} (TV series)", $"{showTitle} (American TV series)", $"{showTitle} (British TV series)", showTitle };

        foreach (var title in titles)
        {
            using var response = await Http.GetAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title.Replace(' ', '_'))}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }
            response.EnsureSuccessStatusCode();
            var summary = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var description = summary.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            if (articleUrl is null && !TvSeries().IsMatch(description))
            {
                continue;
            }

            var pageTitle = summary.GetProperty("title").GetString() ?? title;
            var url = summary.TryGetProperty("content_urls", out var urls) ? urls.GetProperty("desktop").GetProperty("page").GetString()! : articleUrl!;
            var extract = summary.TryGetProperty("extract", out var e) ? e.GetString() ?? "" : "";
            var intro = await IntroAsync(pageTitle, ct) ?? extract;

            var facts = new List<KeyValuePair<string, string>> { new("wikipedia", url) };
            if (extract.Length > 0)
            {
                facts.Add(new("summary", extract));
            }
            return new FoundSource(SourceKinds.Wikipedia, url, $"Wikipedia: {pageTitle}",
                Text.Clip((description.Length > 0 ? description + ". " : "") + intro, ResearchSource.MaxExcerpt), facts);
        }
        return null;
    }

    /// <summary>The article's introduction as plain text, or null when the MediaWiki API refuses (it rate-limits shared addresses).</summary>
    private async Task<string?> IntroAsync(string title, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync("https://en.wikipedia.org/w/api.php?action=query&prop=extracts&exintro=1&explaintext=1&redirects=1&format=json&formatversion=2&titles="
                + Uri.EscapeDataString(title), ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return json.GetProperty("query").GetProperty("pages")[0].TryGetProperty("extract", out var extract) && extract.GetString() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Date(string? value) =>
        value is { Length: >= 10 } && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    [GeneratedRegex(@"\b(television|TV) (series|show|programme|program|miniseries|sitcom|drama|anthology)\b|\bseries\b", RegexOptions.IgnoreCase)]
    private static partial Regex TvSeries();
}

/// <summary>
/// TMDB, when <c>Research:TmdbApiKey</c> is set (a v3 key, or a v4 read token): overview, tagline, network, dates,
/// counts, creators and its users' rating. Found by the TMDB id from Wikidata, else the IMDb id, else a title search
/// in the premiere year.
/// </summary>
public sealed class TmdbSource(IConfiguration config)
{
    private static readonly HttpClient Http = ExternalSources.CreateClient();

    public bool Configured => config["Research:TmdbApiKey"] is { Length: > 0 };

    public async Task<FoundSource?> GatherAsync(CatalogShow show, IReadOnlyList<FoundSource> found, CancellationToken ct)
    {
        var known = found.SelectMany(f => f.Facts).ToList();
        var id = known.FirstOrDefault(f => f.Key == "tmdb").Value is { } tmdbUrl ? tmdbUrl[(tmdbUrl.LastIndexOf('/') + 1)..] : null;
        if (id is null && known.FirstOrDefault(f => f.Key == "imdb").Value is { } imdbUrl)
        {
            var imdb = imdbUrl.TrimEnd('/')[(imdbUrl.TrimEnd('/').LastIndexOf('/') + 1)..];
            var find = await GetAsync($"find/{imdb}?external_source=imdb_id", ct);
            id = find?.GetProperty("tv_results").EnumerateArray().Select(r => r.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture)).FirstOrDefault();
        }
        if (id is null)
        {
            var year = show.FirstAirDate?.Year is { } y ? $"&first_air_date_year={y}" : "";
            var search = await GetAsync($"search/tv?query={Uri.EscapeDataString(show.Title)}{year}", ct);
            id = search?.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture)).FirstOrDefault();
        }
        if (id is null || await GetAsync($"tv/{id}", ct) is not { } tv)
        {
            return null;
        }

        string? Str(string name) => tv.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        int? Int(string name) => tv.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        IEnumerable<string> Names(string name) => tv.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetProperty("name").GetString()!).Where(s => s.Length > 0)
            : [];

        var url = $"https://www.themoviedb.org/tv/{id}";
        var facts = new List<KeyValuePair<string, string>> { new("tmdb", url) };
        void Add(string key, string? value) { if (value is { Length: > 0 }) facts.Add(new(key, value)); }
        Add("summary", Str("overview"));
        Add("tagline", Str("tagline"));
        Add("network", Names("networks").FirstOrDefault());
        Add("premiered", Str("first_air_date"));
        Add("ended", Str("status") is "Ended" or "Canceled" ? Str("last_air_date") : null);
        Add("seasons", Int("number_of_seasons")?.ToString(CultureInfo.InvariantCulture));
        Add("episodes", Int("number_of_episodes")?.ToString(CultureInfo.InvariantCulture));
        Add("creators", string.Join(", ", Names("created_by")));
        if (tv.TryGetProperty("vote_average", out var avg) && tv.TryGetProperty("vote_count", out var count) && count.GetInt32() > 0)
        {
            Add("tmdbRating", $"{avg.GetDouble():0.0}/10 from {count.GetInt32():N0} votes");
        }

        var excerpt = string.Join("\n", new[]
        {
            Str("overview"), Str("tagline") is { } t ? $"Tagline: {t}" : null, Str("status") is { } st ? $"Status: {st}" : null,
            $"Genres: {string.Join(", ", Names("genres"))}", $"Production: {string.Join(", ", Names("production_companies"))}"
        }.Where(l => l is { Length: > 0 }));
        return new FoundSource(SourceKinds.Tmdb, url, $"TMDB: {Str("name") ?? show.Title}", Text.Clip(excerpt, ResearchSource.MaxExcerpt), facts);
    }

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        var key = config["Research:TmdbApiKey"]!;
        var v4 = key.Length > 40;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.themoviedb.org/3/{path}" + (v4 ? "" : $"{(path.Contains('?') ? '&' : '?')}api_key={key}"));
        if (v4)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}
