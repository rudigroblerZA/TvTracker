namespace Catalog.Data;

/// <summary>What a where-to-watch link opens: the service's site, its search for the show, or the show's own page.</summary>
public enum WatchLink
{
    Site,
    Search,
    Title
}

/// <summary>A streaming service the catalog knows how to link into.</summary>
/// <param name="Name">The name shown for a network show listed under the service (House of the Dragon on HBO Max).</param>
/// <param name="Hosts">Its web hosts, without "www.", optionally with a path ("amazon.com/gp/video"); a subdomain of one
/// counts too, and a path matches whole segments only.</param>
/// <param name="TitlePaths">Path prefixes of its show pages ("/title/" on Netflix); an address under one, with more after
/// it, is a show's page. Empty when its show pages can't be told from its other pages.</param>
/// <param name="Search">Its search page, with <c>{q}</c> for the show's title; null when it has none that works signed out.</param>
public sealed record StreamingService(string Name, IReadOnlyList<string> Hosts, IReadOnlyList<string> TitlePaths, string? Search = null);

/// <summary>
/// Where-to-watch links. TVmaze gives a streaming show's service ("Netflix", with its home page) and, separately, the
/// show's official site, which for most streaming shows is its page on that service
/// ("https://www.netflix.com/title/80057281"). That page is the link to keep: on a phone, the OS opens it in the
/// service's app when it's installed (universal and app links), and in the browser otherwise.
/// <para>
/// Only a known service's show-page paths count as a show's page, so a home page with a path ("/browse", a country)
/// or a site that isn't the service is never taken for one. Otherwise the link is the service's search for the title,
/// when it has one that works signed out, else its site.
/// </para>
/// </summary>
public static class StreamingServices
{
    public static readonly IReadOnlyList<StreamingService> Known =
    [
        new("Netflix", ["netflix.com"], ["/title/"], "https://www.netflix.com/search?q={q}"),
        new("Prime Video", ["primevideo.com", "amazon.com/gp/video", "amazon.co.uk/gp/video"], ["/detail/", "/gp/video/detail/"],
            "https://www.primevideo.com/search/?phrase={q}"),
        new("Apple TV", ["tv.apple.com"], ["/show/"], "https://tv.apple.com/search?term={q}"),
        new("Disney+", ["disneyplus.com"], ["/series/"]),
        new("HBO Max", ["hbomax.com", "max.com"], ["/show/", "/shows/"]),
        new("Hulu", ["hulu.com"], ["/series/"], "https://www.hulu.com/search?q={q}"),
        new("Paramount+", ["paramountplus.com"], ["/shows/"]),
        new("Peacock", ["peacocktv.com"], ["/watch/asset/", "/stream-tv/"]),
        new("AMC+", ["amcplus.com"], ["/shows/"]),
        new("BBC iPlayer", ["bbc.co.uk/iplayer"], ["/iplayer/episodes/"], "https://www.bbc.co.uk/iplayer/search?q={q}"),
        new("ITVX", ["itvx.com"], ["/shows/"]),
        new("Channel 4", ["channel4.com"], ["/programmes/"]),
        new("Crunchyroll", ["crunchyroll.com"], ["/series/"], "https://www.crunchyroll.com/search?q={q}"),
        new("Acorn TV", ["acorn.tv"], ["/series/"]),
        new("MGM+", ["mgmplus.com"], ["/series/"]),
        new("ABC iview", ["iview.abc.net.au"], ["/show/"]),
        new("Binge", ["binge.com.au"], ["/shows/"]),
        new("YouTube", ["youtube.com"], [], "https://www.youtube.com/results?search_query={q}"),
        new("CBC Gem", ["gem.cbc.ca"], []),
        new("NOW", ["nowtv.com"], [])
    ];

    /// <summary>The known service an address belongs to, if any.</summary>
    public static StreamingService? For(string? url) =>
        Parse(url) is { } uri ? Known.FirstOrDefault(s => s.Hosts.Any(h => Matches(uri, h))) : null;

    /// <summary>Whether the address is a show's page on a known service (see <see cref="StreamingService.TitlePaths"/>).</summary>
    public static bool IsTitlePage(string? url) =>
        Parse(url) is { } uri && For(url) is { } service
        && service.TitlePaths.Any(p => uri.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                                       && uri.AbsolutePath.Length > p.Length);

    /// <summary>
    /// Whether two addresses belong to the same known service, or, when neither is a known service's, the same host.
    /// </summary>
    public static bool SameService(string? a, string? b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y)
        {
            return false;
        }
        var (first, second) = (For(a), For(b));
        return first is not null || second is not null
            ? first == second
            : string.Equals(Host(x), Host(y), StringComparison.OrdinalIgnoreCase);
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
        var onHost = string.Equals(h, domain, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        // Whole segments: "/iplayer" matches "/iplayer" and "/iplayer/…", not "/iplayerradio".
        return onHost && (path.Length == 0
            || string.Equals(uri.AbsolutePath.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string Host(Uri uri) => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;

    private static Uri? Parse(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
}
