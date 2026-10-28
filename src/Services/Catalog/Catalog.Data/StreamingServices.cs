namespace Catalog.Data;

/// <summary>A streaming service the catalog knows how to link into.</summary>
/// <param name="Name">The name shown when the provider gives none ("HBO Max" for a show page on hbo.com).</param>
/// <param name="Hosts">Its web hosts, without "www."; a subdomain of one counts too.</param>
/// <param name="Search">Its search page, with <c>{q}</c> for the show's title; null when it has none that works signed out.</param>
public sealed record StreamingService(string Name, IReadOnlyList<string> Hosts, string? Search = null);

/// <summary>
/// Where-to-watch links. TVmaze gives a streaming show's service ("Netflix", with its home page) and, separately, the
/// show's official site, which for most streaming shows is its page on that service
/// ("https://www.netflix.com/title/80057281"). That page is the link to keep: on a phone, the OS opens it in the
/// service's app when it's installed (universal and app links), and in the browser otherwise. A service's home page is
/// only a fallback, and a known service's search for the title beats it.
/// </summary>
public static class StreamingServices
{
    public static readonly IReadOnlyList<StreamingService> Known =
    [
        new("Netflix", ["netflix.com"], "https://www.netflix.com/search?q={q}"),
        new("Prime Video", ["primevideo.com", "amazon.com", "amazon.co.uk"], "https://www.primevideo.com/search/?phrase={q}"),
        new("Apple TV", ["tv.apple.com"], "https://tv.apple.com/search?term={q}"),
        new("Disney+", ["disneyplus.com"]),
        new("HBO Max", ["hbomax.com", "max.com", "hbo.com"]),
        new("Hulu", ["hulu.com"], "https://www.hulu.com/search?q={q}"),
        new("Paramount+", ["paramountplus.com"]),
        new("Peacock", ["peacocktv.com"]),
        new("AMC+", ["amcplus.com"]),
        new("BBC iPlayer", ["bbc.co.uk/iplayer"], "https://www.bbc.co.uk/iplayer/search?q={q}"),
        new("ITVX", ["itvx.com"]),
        new("Channel 4", ["channel4.com"]),
        new("Crunchyroll", ["crunchyroll.com"], "https://www.crunchyroll.com/search?q={q}"),
        new("YouTube", ["youtube.com"], "https://www.youtube.com/results?search_query={q}"),
        new("Acorn TV", ["acorn.tv"]),
        new("MGM+", ["mgmplus.com"]),
        new("Starz", ["starz.com"]),
        new("Showtime", ["sho.com"]),
        new("CBC Gem", ["gem.cbc.ca"]),
        new("ABC iview", ["iview.abc.net.au"]),
        new("Binge", ["binge.com.au"]),
        new("NOW", ["nowtv.com"])
    ];

    /// <summary>The known service an address belongs to, if any.</summary>
    public static StreamingService? For(string? url) =>
        Parse(url) is { } uri ? Known.FirstOrDefault(s => s.Hosts.Any(h => Matches(uri, h))) : null;

    /// <summary>
    /// Whether the address is a page of its own on its site (a show's), not the site's home page or a section of it
    /// ("https://www.netflix.com/", "https://www.bbc.co.uk/iplayer", "https://www.mgmplus.com/browse").
    /// </summary>
    public static bool IsTitlePage(string? url)
    {
        if (Parse(url) is not { } uri)
        {
            return false;
        }
        var prefix = For(url)?.Hosts.Select(h => h.IndexOf('/') is > 0 and var slash ? h[slash..] : "")
            .FirstOrDefault(p => p.Length > 0 && uri.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ?? "";
        var rest = uri.AbsolutePath[prefix.Length..].Trim('/');
        return rest.Length > 0 && !GenericPaths.Contains(rest);
    }

    private static readonly HashSet<string> GenericPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "browse", "home", "index.html", "en", "en-us", "en-gb", "us", "uk", "gb", "za"
    };

    /// <summary>Whether two addresses are on the same service's site (or the same host, for unknown services).</summary>
    public static bool SameService(string? a, string? b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y)
        {
            return false;
        }
        return For(a) is { } service ? service == For(b) : string.Equals(Host(x), Host(y), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A search of the service (known by its address, else its name) for a title, when it has one that works signed out.
    /// </summary>
    public static string? SearchFor(string? url, string name, string title) =>
        (For(url) ?? Known.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            ?.Search?.Replace("{q}", Uri.EscapeDataString(title));

    private static bool Matches(Uri uri, string host)
    {
        var (domain, path) = host.IndexOf('/') is > 0 and var slash ? (host[..slash], host[slash..]) : (host, "");
        var h = Host(uri);
        return (string.Equals(h, domain, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            && uri.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase);
    }

    private static string Host(Uri uri) => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;

    private static Uri? Parse(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
}
