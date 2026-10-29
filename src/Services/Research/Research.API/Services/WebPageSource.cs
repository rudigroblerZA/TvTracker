using Research.API.Data;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Research.API.Services;

/// <summary>
/// A page a researcher adds by address: a review, an interview, an article. Its readable text becomes the source.
/// <para>
/// The server fetches it on a moderator's behalf, so only public web addresses are allowed: http(s) on the standard
/// ports, to hosts that resolve only to public IP addresses (no loopback, private, link-local or carrier-grade NAT
/// ranges), checked again at every redirect (at most 3). Pages are capped at 2 MB, HTML or plain text only, 15 s.
/// The check is on the name's addresses before connecting, so a host that changes its DNS answer between the check and
/// the request (DNS rebinding) isn't caught; the requests go through the environment's proxy where there is one.
/// </para>
/// </summary>
public sealed class WebPageSource(ILogger<WebPageSource> logger)
{
    private const int MaxBytes = 2 * 1024 * 1024;

    // Its own client: no automatic redirects (each is checked), and none of the standard resilience retries.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "TvTracker-research/1.0 (+https://github.com/rudigroblerZA/TvTracker)" } }
    };

    /// <summary>The page as a source, or the reason it can't be added.</summary>
    public async Task<(FoundSource? Source, string? Problem)> FetchAsync(string address, CancellationToken ct)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri))
        {
            return (null, "That isn't a web address.");
        }

        for (var hop = 0; hop <= 3; hop++)
        {
            if (await CheckAsync(uri, ct) is { } problem)
            {
                return (null, problem);
            }

            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"The page answered {(int)response.StatusCode}.");
            }

            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type is not ("text/html" or "text/plain" or "application/xhtml+xml"))
            {
                return (null, $"Only web pages and plain text can be added (this is {type}).");
            }

            var bytes = await ReadCappedAsync(response, ct);
            var charset = response.Content.Headers.ContentType?.CharSet;
            var body = ((charset is { Length: > 0 } ? TryEncoding(charset) : null) ?? Encoding.UTF8).GetString(bytes);
            var (title, text) = type == "text/plain" ? (uri.Host, Text.Collapse(body)) : Text.FromHtml(body);
            if (text.Length < 40)
            {
                return (null, "The page has no readable text (it may need JavaScript to show anything).");
            }

            logger.LogInformation("Read {Url} for research ({Length} characters)", uri, text.Length);
            return (new FoundSource(SourceKinds.Url, uri.ToString(), Text.Clip(title is { Length: > 0 } ? title : uri.Host, 500),
                Text.Clip(text, ResearchSource.MaxExcerpt), []), null);
        }
        return (null, "The page redirected too many times.");
    }

    private static async Task<string?> CheckAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort)
        {
            return "Only http and https addresses on the standard ports can be added.";
        }
        if (uri.HostNameType is UriHostNameType.Dns && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !uri.Host.Contains('.')))
        {
            return "That address isn't on the public web.";
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (SocketException)
        {
            return $"{uri.Host} doesn't resolve.";
        }
        return addresses.Length == 0 || addresses.Any(a => !IsPublic(a)) ? "That address isn't on the public web." : null;
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || (address.GetAddressBytes()[0] & 0xFE) == 0xFC || address.Equals(IPAddress.IPv6Any));
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0 && buffer.Length < MaxBytes)
        {
            buffer.Write(chunk, 0, Math.Min(read, MaxBytes - (int)buffer.Length));
        }
        return buffer.ToArray();
    }

    private static Encoding? TryEncoding(string charset)
    {
        try { return Encoding.GetEncoding(charset.Trim('"')); }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>Plain text from sources: HTML to readable text, whitespace, and length.</summary>
public static partial class Text
{
    /// <summary>At most <paramref name="max"/> characters, cut at a space where there is one, with an ellipsis.</summary>
    public static string Clip(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }
        var cut = text.LastIndexOf(' ', max - 2);
        return text[..(cut > 0 ? cut : max - 1)] + "…";
    }

    public static string Collapse(string text) => Spaces().Replace(text, " ").Trim();

    /// <summary>The page's title, and the text of its article or main element (else its body), without scripts and chrome.</summary>
    public static (string? Title, string Text) FromHtml(string html)
    {
        var title = OgTitle().Match(html) is { Success: true } og ? og.Groups[1].Value : TitleTag().Match(html) is { Success: true } t ? t.Groups[1].Value : null;
        var body = Noise().Replace(html, " ");
        var main = Article().Match(body) is { Success: true } article ? article.Groups[1].Value : MainTag().Match(body) is { Success: true } m ? m.Groups[1].Value : body;
        var text = WebUtility.HtmlDecode(Breaks().Replace(Tags().Replace(Breaks().Replace(main, "\n"), " "), "\n"));
        return (title is null ? null : Collapse(WebUtility.HtmlDecode(title)), Collapse(text));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?is)<(script|style|noscript|nav|footer|header|aside|form|svg|iframe)\b[^>]*>.*?</\1>")]
    private static partial Regex Noise();

    [GeneratedRegex(@"(?is)<article\b[^>]*>(.*)</article>")]
    private static partial Regex Article();

    [GeneratedRegex(@"(?is)<main\b[^>]*>(.*)</main>")]
    private static partial Regex MainTag();

    [GeneratedRegex(@"(?is)<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"(?i)</?(p|br|div|li|h[1-6])\b[^>]*>")]
    private static partial Regex Breaks();

    [GeneratedRegex(@"(?is)<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)[""']")]
    private static partial Regex OgTitle();

    [GeneratedRegex(@"(?is)<title[^>]*>(.*?)</title>")]
    private static partial Regex TitleTag();
}
