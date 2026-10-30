using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Catalog.Data;

namespace Catalog.SyncWorker.Quotes;

/// <summary>Wikimedia answered 429; <see cref="RetryAfter"/> is how long it asked to wait, when it said.</summary>
public sealed class WikimediaBusyException(TimeSpan? retryAfter) : Exception("Wikimedia answered 429 Too Many Requests.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Wikidata's query service, to find shows' Wikiquote pages by TVmaze id (P8600, as Research finds shows), and
/// Wikiquote's raw wikitext (<c>index.php?action=raw</c>). The raw endpoint, not the REST HTML one: from the sandbox's
/// shared address the REST API answered 429 to nearly every request, the raw one to almost none.
/// <para>
/// A plain client with one try, as Research's sources: the standard resilience handler would retry a 429 until its
/// timeout. A 429 is thrown as <see cref="WikimediaBusyException"/> for the caller to wait out.
/// </para>
/// </summary>
public static partial class WikiquoteClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "TvTracker-quotes/1.0 (+https://github.com/rudigroblerZA/TvTracker)" } }
    };

    private const string Sparql = """
        SELECT ?tvmaze ?page WHERE {
          VALUES ?tvmaze { {IDS} }
          ?item wdt:P8600 ?tvmaze.
          ?page schema:about ?item; schema:isPartOf <https://en.wikiquote.org/>.
        }
        """;

    /// <summary>Each TVmaze id's Wikiquote page title ("Friends (TV series)"); ids without one are left out.</summary>
    public static async Task<Dictionary<string, string>> FindPagesAsync(IEnumerable<string> tvmazeIds, CancellationToken ct)
    {
        var values = string.Join(' ', tvmazeIds.Select(id => $"\"{id}\""));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://query.wikidata.org/sparql")
        {
            Content = new FormUrlEncodedContent([new("query", Sparql.Replace("{IDS}", values))])
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));
        using var response = await SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var pages = new Dictionary<string, string>();
        foreach (var row in json.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray())
        {
            var id = row.GetProperty("tvmaze").GetProperty("value").GetString();
            var url = row.GetProperty("page").GetProperty("value").GetString();
            if (id is not null && url is not null)
            {
                // Two items with one TVmaze id (a show and its franchise): keep the first.
                pages.TryAdd(id, Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]).Replace('_', ' '));
            }
        }
        return pages;
    }

    /// <summary>A page's wikitext, following one redirect; null when the page doesn't exist.</summary>
    public static async Task<string?> GetWikitextAsync(string page, CancellationToken ct)
    {
        for (var hop = 0; hop < 2; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ShowQuoteSource.PageUrl(page, raw: true));
            using var response = await SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(ct);
            if (Redirect().Match(text) is not { Success: true } redirect)
            {
                return text;
            }
            page = redirect.Groups[1].Value.Trim();
        }
        return null;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var after = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            throw new WikimediaBusyException(after);
        }
        return response;
    }

    [GeneratedRegex(@"^\s*#REDIRECT\s*\[\[([^\]|#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Redirect();
}
