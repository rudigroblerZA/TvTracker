namespace Catalog.Data;

/// <summary>
/// One click on a where-to-watch link, recorded by Catalog.API before it redirects (affiliate links). Nothing about who
/// clicked is kept (no user id, address or browser): what's measured is which links send people where, and the id is
/// what an affiliate network sends back with a conversion.
/// </summary>
public class OutboundClick
{
    /// <summary>Set by the app; also the click id an affiliate link carries (<c>{click}</c>) and its network posts back.</summary>
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    /// <summary>The provider's name as the show lists it ("Netflix").</summary>
    public required string Service { get; set; }

    public WatchProviderType Type { get; set; }

    /// <summary>What the link opened: the show's page on the service, a search there, or its site.</summary>
    public WatchLink Link { get; set; }

    /// <summary>ISO 3166-1 country code of the listing.</summary>
    public required string Region { get; set; }

    /// <summary>Where in the app the link was (<see cref="OutboundSurfaces"/>).</summary>
    public required string Surface { get; set; }

    /// <summary>The plan the caller gets ("free", "plus", "family"), "signed-out", or "unknown" while plans don't apply.</summary>
    public required string Plan { get; set; }

    /// <summary>The affiliate program whose link was used; null when the service has none (a plain link, still counted).</summary>
    public string? Program { get; set; }

    public DateTime ClickedAtUtc { get; set; }
}

/// <summary>
/// A sale or sign-up an affiliate network reported for a click (its server-to-server postback). The amount is the
/// commission in the program's currency, as the network reports it.
/// </summary>
public class AffiliateConversion
{
    public Guid Id { get; set; }

    public Guid ClickId { get; set; }

    public required string Program { get; set; }

    /// <summary>The network's id for the conversion, unique per program, so a repeated postback isn't counted twice.</summary>
    public required string TransactionId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>ISO 4217 code: the program's currency.</summary>
    public required string Currency { get; set; }

    public DateTime ReceivedAtUtc { get; set; }
}

/// <summary>Where a tracked link sits in the app.</summary>
public static class OutboundSurfaces
{
    /// <summary>The show page's "Watch on …" button.</summary>
    public const string WatchNow = "watch-now";

    /// <summary>The show page's "Where to watch" list.</summary>
    public const string WhereToWatch = "where-to-watch";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { WatchNow, WhereToWatch };
}
