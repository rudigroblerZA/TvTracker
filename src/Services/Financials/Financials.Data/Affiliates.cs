namespace Financials.Data;

/// <summary>
/// One click on a where-to-watch link, recorded by Catalog.API before it redirects (affiliate links). Nothing about who
/// clicked is kept (no user id, address or browser): what's measured is which links send people where, and the id is
/// what an affiliate network sends back with a conversion.
/// </summary>
public class OutboundClick
{
    /// <summary>
    /// Set by the app; also the click id an affiliate link carries (<c>{click}</c>) and its network posts back.
    /// </summary>
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    /// <summary>
    /// The show's title when it was clicked, kept so the report needn't ask Catalog.API.
    /// </summary>
    public required string ShowTitle { get; set; }

    /// <summary>
    /// The provider's name as the show lists it ("Netflix").
    /// </summary>
    public required string Service { get; set; }

    /// <summary>
    /// The kind of listing clicked (stream, rent, buy…), as Catalog.API names it.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// What the link opened (the show's page on the service, a search there, or its site), as Catalog.API names it.
    /// </summary>
    public required string Link { get; set; }

    /// <summary>
    /// ISO 3166-1 country code of the listing.
    /// </summary>
    public required string Region { get; set; }

    /// <summary>
    /// Where in the app the link was (<see cref="OutboundSurfaces"/>).
    /// </summary>
    public required string Surface { get; set; }

    /// <summary>
    /// The plan the caller gets ("free", "plus", "family"), "signed-out", or "unknown" while plans don't apply.
    /// </summary>
    public required string Plan { get; set; }

    /// <summary>
    /// The affiliate program whose link was used; null when the service has none (a plain link, still counted).
    /// </summary>
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

    /// <summary>
    /// The network's id for the conversion, unique per program, so a repeated postback isn't counted twice.
    /// </summary>
    public required string TransactionId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>
    /// ISO 4217 code: the program's currency.
    /// </summary>
    public required string Currency { get; set; }

    public DateTime ReceivedAtUtc { get; set; }
}

/// <summary>
/// An affiliate program an admin set up in Settings: which services and regions it covers and the network's tracked
/// link. Programs in Financials.API's configuration (Affiliates:Programs) still work; one here with the same name wins.
/// </summary>
public class AffiliateProgramRecord
{
    /// <summary>
    /// The program's name, as the network's postback gives it ("netflix-us"); lower case, the key.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The services it covers, as where-to-watch names them ("Netflix"); "*" for every service.
    /// </summary>
    public List<string> Services { get; set; } = [];

    /// <summary>
    /// Two-letter regions; none for every region.
    /// </summary>
    public List<string> Regions { get; set; } = [];

    /// <summary>
    /// The network's link, with {url} for the service's own link and {click} for the click id. Empty for a test
    /// program, which takes the test network's.
    /// </summary>
    public string Template { get; set; } = "";

    /// <summary>
    /// What the network's postback must give as its key. Empty for a test program.
    /// </summary>
    public string? PostbackKey { get; set; }

    public string Currency { get; set; } = "USD";

    /// <summary>
    /// A stand-in until there's a real deal: clicks go through the built-in test network (a page where a sale can be
    /// reported by hand), where one is running; elsewhere the program isn't used.
    /// </summary>
    public bool Test { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
