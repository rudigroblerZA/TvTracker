using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Shared by Financials.API (which owns the affiliate programs, and what clicks and sales came of them) and Catalog.API
// (which builds each where-to-watch link from its copy of the programs), so a link is built one way.

/// <summary>
/// One affiliate program: the services and regions it covers, the link it wraps a service's address in, and the key its
/// network sends with a postback.
/// </summary>
public sealed class AffiliateProgram
{
    /// <summary>
    /// Its name in reports and in its postback address (<c>/affiliates/postback/{name}</c>).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Provider names as the catalog lists them ("Netflix"), compared ignoring case; <c>*</c> covers every service (the
    /// development test network).
    /// </summary>
    public List<string> Services { get; set; } = [];

    /// <summary>
    /// ISO 3166-1 codes it pays for; empty means every region.
    /// </summary>
    public List<string> Regions { get; set; } = [];

    /// <summary>
    /// The tracked link, https only (or http to this machine, for the development test network): <c>{url}</c> is the service's address (escaped) and <c>{click}</c> the click id the
    /// network passes back, e.g. <c>https://network.example/c/123?u={url}&amp;subId1={click}</c>.
    /// </summary>
    public string Template { get; set; } = "";

    /// <summary>
    /// The secret its network adds to postbacks (<c>key=</c>); without one, its postbacks are refused.
    /// </summary>
    public string? PostbackKey { get; set; }

    /// <summary>
    /// ISO 4217 code its commissions are paid in.
    /// </summary>
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Where the program comes from: <see cref="AffiliateSources"/>. Set by the app, not read from configuration.
    /// </summary>
    public string Source { get; set; } = AffiliateSources.Configuration;

    /// <summary>
    /// A stand-in that goes through the test network.
    /// </summary>
    public bool Test { get; set; }

    /// <summary>
    /// False for a program switched off in Settings: links don't use it, but a sale for a click already sent through it
    /// still counts.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Where an affiliate program is defined.
/// </summary>
public static class AffiliateSources
{
    /// <summary>
    /// Set up by an admin in Settings (the affiliate_programs table); can be changed there.
    /// </summary>
    public const string Settings = "settings";

    /// <summary>
    /// Affiliates:Programs in Financials.API's configuration.
    /// </summary>
    public const string Configuration = "configuration";

    /// <summary>
    /// The development test network's own catch-all, covering every service no program does.
    /// </summary>
    public const string TestNetwork = "test-network";
}

/// <summary>
/// Choosing the program for a where-to-watch link and building the link.
/// </summary>
public static class AffiliateLinks
{
    /// <summary>
    /// The first valid, switched-on program covering the service in the region, if any.
    /// </summary>
    public static AffiliateProgram? For(IEnumerable<AffiliateProgram> programs, string service, string region) =>
        programs.FirstOrDefault(p => p.Enabled
            && (p.Services.Contains(service, StringComparer.OrdinalIgnoreCase) || p.Services.Contains(AnyService))
            && (p.Regions.Count == 0 || p.Regions.Contains(region, StringComparer.OrdinalIgnoreCase))
            && IsValid(p));

    /// <summary>
    /// In <see cref="AffiliateProgram.Services"/>, every service.
    /// </summary>
    public const string AnyService = "*";

    /// <summary>
    /// The program's tracked link for <paramref name="url"/> and the click.
    /// </summary>
    public static string Wrap(AffiliateProgram program, string url, Guid clickId) =>
        program.Template.Replace("{url}", Uri.EscapeDataString(url)).Replace("{click}", clickId.ToString("N"))
            .Replace("{program}", Uri.EscapeDataString(program.Name));

    /// <summary>
    /// A name, a service, a currency code, and a template that gives an absolute https address (or http to this machine,
    /// as the development test network's is): anything else would send people somewhere broken, so an invalid program is
    /// skipped (and shown as such in the report).
    /// </summary>
    public static bool IsValid(AffiliateProgram program) =>
        program.Name.Length is > 0 and <= 64 && program.Services.Count > 0 && program.Currency.Length == 3
        && Uri.TryCreate(Wrap(program, "https://example.org/", Guid.Empty), UriKind.Absolute, out var uri) && uri.Host.Length > 0
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}

/// <summary>
/// Where a tracked link sits in the app.
/// </summary>
public static class OutboundSurfaces
{
    /// <summary>
    /// The show page's "Watch on …" button.
    /// </summary>
    public const string WatchNow = "watch-now";

    /// <summary>
    /// The show page's "Where to watch" list.
    /// </summary>
    public const string WhereToWatch = "where-to-watch";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { WatchNow, WhereToWatch };
}

/// <summary>
/// A program as Catalog.API needs it to build a link: no postback key, which only Financials.API checks.
/// </summary>
public sealed record AffiliateProgramLink(string Name, IReadOnlyList<string> Services, IReadOnlyList<string> Regions, string Template, string Currency,
    bool Enabled)
{
    public static AffiliateProgramLink From(AffiliateProgram p) => new(p.Name, p.Services, p.Regions, p.Template, p.Currency, p.Enabled);

    public AffiliateProgram ToProgram() =>
        new() { Name = Name, Services = [.. Services], Regions = [.. Regions], Template = Template, Currency = Currency, Enabled = Enabled };
}

/// <summary>
/// Every affiliate program in force, in the order they're tried, as of <paramref name="AsOfUtc"/>. Published by
/// Financials.API when an admin changes one, when it starts, and hourly, so Catalog.API's copy heals; the newest wins.
/// </summary>
public record AffiliateProgramsChangedIntegrationEvent(IReadOnlyList<AffiliateProgramLink> Programs, DateTimeOffset AsOfUtc) : IntegrationEvent;

/// <summary>
/// Someone followed a where-to-watch link. Published by Catalog.API, which built the link; Financials.API keeps the
/// click, so a sale the network reports later can be matched to it.
/// </summary>
/// <param name="Program">The affiliate program the link went through; null for a plain link.</param>
/// <param name="Plan">The clicker's plan, or that they were signed out.</param>
public record OutboundClickedIntegrationEvent(Guid ClickId, Guid ShowId, string ShowTitle, string Service, string Type, string Link, string Region,
    string Surface, string Plan, string? Program, DateTimeOffset ClickedAtUtc) : IntegrationEvent;
