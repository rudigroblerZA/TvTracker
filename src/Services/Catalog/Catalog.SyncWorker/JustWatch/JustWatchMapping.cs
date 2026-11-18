using System.Web;
using Catalog.Data;

namespace Catalog.SyncWorker.JustWatch;

/// <summary>
/// One where-to-watch row from JustWatch.
/// </summary>
public sealed record JustWatchOption(string ProviderName, WatchProviderType Type, string Url);

/// <summary>
/// What JustWatch's answers mean here, apart from the network and the database.
/// </summary>
public static class JustWatchMapping
{
    public const string Site = "https://www.justwatch.com";

    /// <summary>
    /// The locale to ask a country's offers in: its English one when it has several, else its first. Null when
    /// JustWatch isn't active there.
    /// </summary>
    public static string? LocaleFor(string region, IEnumerable<JustWatchCountry> countries) =>
        countries
            .Where(c => c is { FullLocale: { } locale, Status: null or "active" } && locale.EndsWith("_" + region, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.FullLocale!)
            .OrderByDescending(l => l.StartsWith("en_", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    /// <summary>
    /// Whether a title found by name and year is the show asked for: the same title (or original title), give or take
    /// case and punctuation, premiered within a year of ours. A lookup by IMDb id needs no such check.
    /// </summary>
    public static bool IsSameShow(JustWatchTitle found, string title, int year) =>
        found.OriginalReleaseYear is { } foundYear && Math.Abs(foundYear - year) <= 1
        && new[] { found.Title, found.OriginalTitle }.Any(t => t is not null && Normalize(t) == Normalize(title));

    /// <summary>
    /// The title's page on justwatch.com, for the credit; null when the answer has none.
    /// </summary>
    public static string? PathOf(JustWatchTitle title) =>
        title.FullPath is { Length: > 1 and <= 512 } path && path.StartsWith('/') && !path.StartsWith("//") ? path : null;

    /// <summary>
    /// The offers as where-to-watch rows: one for each service and way of watching, whatever the picture quality.
    /// <list type="bullet">
    /// <item><b>Name:</b> the catalog's own name for a service it knows ("Prime Video", not "Amazon Prime Video"), so
    /// affiliate programs, logos and "your services" keep matching; else JustWatch's.</item>
    /// <item><b>Link:</b> JustWatch's attributed one, untouched. An offer without a usable one is left out.</item>
    /// </list>
    /// </summary>
    public static List<JustWatchOption> Options(JustWatchTitle title, IReadOnlyDictionary<long, string> providers) =>
        (title.Offers ?? [])
            .Select(o => (Offer: o, Type: TypeOf(o.MonetizationType), Url: o.Urls?.StandardWeb))
            .Where(o => o.Type is not null && IsWebAddress(o.Url))
            .Select(o => (Name: NameOf(o.Offer, providers), Type: o.Type!.Value, Url: o.Url!))
            .Where(o => o.Name is not null)
            .DistinctBy(o => (o.Name, o.Type))
            .Select(o => new JustWatchOption(o.Name!, o.Type, o.Url))
            .ToList();

    internal static WatchProviderType? TypeOf(string? monetization) => monetization?.ToLowerInvariant() switch
    {
        "flatrate" => WatchProviderType.Stream,
        "free" or "ads" => WatchProviderType.Free,
        "rent" => WatchProviderType.Rent,
        "buy" => WatchProviderType.Buy,
        _ => null
    };

    private static string? NameOf(JustWatchOffer offer, IReadOnlyDictionary<long, string> providers)
    {
        var name = StreamingServices.For(offer.Urls?.RawWeb ?? TargetOf(offer.Urls?.StandardWeb))?.Name
            ?? providers.GetValueOrDefault(offer.ProviderId);
        return string.IsNullOrWhiteSpace(name) ? null : name.Length > 128 ? name[..128] : name;
    }

    /// <summary>
    /// Where JustWatch's click link ends up (its <c>r</c> parameter), to recognise the service by.
    /// </summary>
    private static string? TargetOf(string? clickUrl) =>
        Uri.TryCreate(clickUrl, UriKind.Absolute, out var uri) ? HttpUtility.ParseQueryString(uri.Query)["r"] : null;

    private static bool IsWebAddress(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string Normalize(string title) => new(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
