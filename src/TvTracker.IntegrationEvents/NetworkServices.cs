namespace TvTracker.IntegrationEvents;

// Shared by Analytics (logos, the "your services" report) and the UI (which links this file), so a network maps to the
// same service and price everywhere. Hand-kept: TVmaze gives a show's network name and nothing about what it costs.

/// <summary>
/// A service people pay for (or watch free) to see a network's shows.
/// </summary>
/// <param name="Key">Stable id, stored with a user's chosen services.</param>
/// <param name="Domain">Its site, whose icon is the logo.</param>
/// <param name="MonthlyUsd">The cheapest US plan a month (with ads where offered), as of <see cref="NetworkServices.PricesAsOf"/>;
/// 0 when free to watch; null when unknown.</param>
/// <param name="Note">What the price is, or how the network is watched ("Free over the air in the US").</param>
public record NetworkService(string Key, string Name, string Domain, decimal? MonthlyUsd, string? Note = null);

public static class NetworkServices
{
    /// <summary>When the prices were checked, and where: US list prices before tax.</summary>
    public const string PricesAsOf = "October 2026";

    public const string PricesSource = "https://stream-wiser.com/prices";

    public static readonly IReadOnlyList<NetworkService> All =
    [
        new("netflix", "Netflix", "netflix.com", 8.99m, "Standard with ads; $19.99 without"),
        new("prime-video", "Prime Video", "primevideo.com", 8.99m, "Standalone with ads; included with Prime ($14.99)"),
        new("hbo-max", "HBO Max", "hbomax.com", 10.99m, "Basic with ads; $18.49 without"),
        new("apple-tv", "Apple TV", "tv.apple.com", 14.99m, "No ad tier"),
        new("paramount-plus", "Paramount+", "paramountplus.com", 8.99m, "Essential with ads; Premium with Showtime $13.99"),
        new("hulu", "Hulu", "hulu.com", 12.49m, "With ads; $21.49 without"),
        new("disney-plus", "Disney+", "disneyplus.com", 12.49m, "With ads; $21.49 without"),
        new("peacock", "Peacock", "peacocktv.com", 8.99m, "Select with ads; Premium $12.99"),
        new("amc-plus", "AMC+", "amcplus.com", 10.99m),
        new("starz", "STARZ", "starz.com", 11.99m),
        new("mgm-plus", "MGM+", "mgmplus.com", null, "Price not confirmed"),
        new("discovery-plus", "discovery+", "discoveryplus.com", null, "Price not confirmed"),
        new("bbc-iplayer", "BBC iPlayer", "bbc.co.uk", null, "UK; needs a TV licence"),
        new("channel-4", "Channel 4", "channel4.com", 0m, "UK; free with ads"),
        new("channel-5", "5", "channel5.com", 0m, "UK; free with ads"),
        new("itvx", "ITVX", "itv.com", 0m, "UK; free with ads"),
        new("pbs", "PBS", "pbs.org", 0m, "Free; Passport for the full library"),
        new("the-cw", "The CW", "cwtv.com", 0m, "Free with ads"),
        new("tencent-video", "Tencent Video", "v.qq.com", null, "China"),
        new("tokyo-mx", "Tokyo MX", "s.mxtv.jp", 0m, "Japan; free over the air")
    ];

    /// <summary>
    /// Where each catalog network's shows are watched (TVmaze's network names). US broadcast networks map to the service
    /// their shows stream on the next day; they're also free over the air.
    /// </summary>
    private static readonly Dictionary<string, string> ServiceOfNetwork = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Netflix"] = "netflix",
        ["Prime Video"] = "prime-video",
        ["HBO"] = "hbo-max",
        ["HBO Max"] = "hbo-max",
        ["Max"] = "hbo-max",
        ["Apple TV"] = "apple-tv",
        ["Apple TV+"] = "apple-tv",
        ["Paramount+"] = "paramount-plus",
        ["Paramount+ with Showtime"] = "paramount-plus",
        ["Showtime"] = "paramount-plus",
        ["CBS"] = "paramount-plus",
        ["Hulu"] = "hulu",
        ["ABC"] = "hulu",
        ["FX"] = "hulu",
        ["FOX"] = "hulu",
        ["Disney+"] = "disney-plus",
        ["Peacock"] = "peacock",
        ["NBC"] = "peacock",
        ["USA Network"] = "peacock",
        ["Syfy"] = "peacock",
        ["Bravo"] = "peacock",
        ["AMC"] = "amc-plus",
        ["AMC+"] = "amc-plus",
        ["STARZ"] = "starz",
        ["MGM+"] = "mgm-plus",
        ["Discovery"] = "discovery-plus",
        ["TLC"] = "discovery-plus",
        ["Food Network"] = "discovery-plus",
        ["BBC One"] = "bbc-iplayer",
        ["BBC Two"] = "bbc-iplayer",
        ["BBC Three"] = "bbc-iplayer",
        ["BBC iPlayer"] = "bbc-iplayer",
        ["Channel 4"] = "channel-4",
        ["5"] = "channel-5",
        ["ITV1"] = "itvx",
        ["ITVX"] = "itvx",
        ["PBS"] = "pbs",
        ["The CW"] = "the-cw",
        ["Tencent QQ"] = "tencent-video",
        ["Tokyo MX"] = "tokyo-mx"
    };

    /// <summary>US broadcast networks, free over the air as well as on their streaming home.</summary>
    public static readonly IReadOnlySet<string> FreeOverTheAir = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "ABC", "CBS", "NBC", "FOX", "The CW", "PBS" };

    private static readonly Dictionary<string, NetworkService> ByKey = All.ToDictionary(s => s.Key);

    public static NetworkService? Get(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The service a network's shows are on; null for a network not in the directory.</summary>
    public static NetworkService? ForNetwork(string? network) =>
        network is not null && ServiceOfNetwork.TryGetValue(network, out var key) ? ByKey[key] : null;

    /// <summary>The catalog networks whose shows are on a service.</summary>
    public static IEnumerable<string> NetworksOf(string key) => ServiceOfNetwork.Where(n => n.Value == key).Select(n => n.Key);
}
