using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Catalog.API.Services;

/// <summary>
/// The affiliate programs the app has joined (<c>Affiliates:Programs</c>); none by default.
/// </summary>
public sealed class AffiliateOptions
{
    public List<AffiliateProgram> Programs { get; set; } = [];
}

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
    /// Affiliates:Programs in Catalog.API's configuration.
    /// </summary>
    public const string Configuration = "configuration";

    /// <summary>
    /// The development test network's own catch-all, covering every service no program does.
    /// </summary>
    public const string TestNetwork = "test-network";
}

/// <summary>
/// Affiliate links: which program covers a click, and the tracked link it becomes. A service with no program gets its
/// plain link, still counted, so the clicks a service would be worth are known before there's a deal.
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

    /// <summary>
    /// The development test network (<c>Affiliates:TestNetwork:Url</c> and <c>:Key</c>, set by the AppHost when run locally
    /// unless <c>Affiliates:TestNetwork=false</c>): every service, every region, its tracked link a page in the web app that can
    /// report a pretend sale through the real postback before sending the viewer on. Null when not configured.
    /// </summary>
    public static AffiliateProgram? TestNetwork(IConfiguration configuration) =>
        configuration["Affiliates:TestNetwork:Url"] is { Length: > 0 } url && configuration["Affiliates:TestNetwork:Key"] is { Length: > 0 } key
            ? new AffiliateProgram
            {
                Name = TestNetworkName,
                Services = [AnyService],
                Template = TestTemplate(url),
                PostbackKey = key,
                Currency = "USD",
                Source = AffiliateSources.TestNetwork,
                Test = true
            }
            : null;

    /// <summary>
    /// The test network's link: its page in the web app, told which program the click belongs to so a sale reported
    /// there is counted against it.
    /// </summary>
    public static string TestTemplate(string webAppUrl) => webAppUrl.TrimEnd('/') + "/dev/affiliate-network?u={url}&click={click}&program={program}";

    public const string TestNetworkName = "test-network";

    /// <summary>
    /// Whether a postback's key is the program's, compared in constant time.
    /// </summary>
    public static bool KeyMatches(AffiliateProgram program, string? key) =>
        program.PostbackKey is { Length: > 0 } expected && key is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(key));
}

/// <summary>
/// Every affiliate program, in the order a click tries them (ones switched off in Settings included, marked so): live ones from Settings, then the
/// configuration's, then Settings' test programs, then the test network's catch-all, so a stand-in never takes a click
/// a real program covers. Kept a few seconds, so a click doesn't read the table each time
/// and a change in Settings applies almost at once (<see cref="Forget"/> makes it at once for the admin who made it).
/// </summary>
public sealed class AffiliatePrograms(CatalogContext db, IOptionsMonitor<AffiliateOptions> options, IConfiguration configuration, IMemoryCache cache)
{
    private const string CacheKey = nameof(AffiliatePrograms);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<AffiliateProgram>> AllAsync(CancellationToken ct)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<AffiliateProgram>? programs))
        {
            programs = Merge(await db.AffiliatePrograms.AsNoTracking().ToListAsync(ct), options.CurrentValue.Programs,
                AffiliateLinks.TestNetwork(configuration));
            cache.Set(CacheKey, programs, CacheFor);
        }
        return programs!;
    }

    public void Forget() => cache.Remove(CacheKey);

    /// <summary>
    /// Settings' live programs, then the configuration's that Settings doesn't name, then Settings'
    /// test programs (with the test network's link and key, or unusable without one), then the test network's
    /// catch-all. Within Settings, a program naming its services comes before one covering every service, then by name.
    /// </summary>
    public static IReadOnlyList<AffiliateProgram> Merge(IEnumerable<AffiliateProgramRecord> records, IEnumerable<AffiliateProgram> configured,
        AffiliateProgram? testNetwork)
    {
        var fromSettings = records
            .OrderBy(r => r.Services.Contains(AffiliateLinks.AnyService)).ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new AffiliateProgram
        {
            Name = r.Name,
            Services = r.Services,
            Regions = r.Regions,
            Template = r.Test ? testNetwork?.Template ?? "" : r.Template,
            PostbackKey = r.Test ? testNetwork?.PostbackKey : r.PostbackKey,
            Currency = r.Currency,
            Source = AffiliateSources.Settings,
            Test = r.Test,
            Enabled = r.Enabled
        }).ToList();
        var named = records.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fromConfiguration = configured.Where(p => p.Source == AffiliateSources.Configuration && !named.Contains(p.Name));
        return [.. fromSettings.Where(p => !p.Test), .. fromConfiguration, .. fromSettings.Where(p => p.Test), .. testNetwork is null ? [] : new[] { testNetwork }];
    }
}

/// <summary>
/// App admins: Keycloak usernames in <c>Admin:Users</c>, as Identity's flags page reads them.
/// </summary>
public static class Admins
{
    public static bool Is(ClaimsPrincipal user, IConfiguration config) =>
        user.FindFirstValue("preferred_username") is { } name
        && (config.GetSection("Admin:Users").Get<string[]>() ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The streaming services given a test program to start with, so their clicks, and sales reported on the test
/// network's page, are tracked per service before any real deal exists. Names are as where-to-watch lists them.
/// </summary>
public static class StarterPrograms
{
    public static readonly IReadOnlyList<(string Name, string Service)> All =
    [
        ("netflix-test", "Netflix"), ("prime-video-test", "Prime Video"), ("disney-plus-test", "Disney+"), ("hulu-test", "Hulu"),
        ("apple-tv-test", "Apple TV"), ("hbo-max-test", "HBO Max"), ("paramount-plus-test", "Paramount+"), ("peacock-test", "Peacock")
    ];

    /// <summary>
    /// The starter programs to add: those whose name isn't taken and whose service no program in
    /// <paramref name="existing"/> names yet, so a service with a program of its own isn't given a stand-in.
    /// </summary>
    public static List<AffiliateProgramRecord> Missing(IReadOnlyCollection<AffiliateProgramRecord> existing, DateTime nowUtc)
    {
        var names = existing.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = existing.SelectMany(r => r.Services).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(p => !names.Contains(p.Name) && !covered.Contains(p.Service))
            .Select(p => new AffiliateProgramRecord
            {
                Name = p.Name, Services = [p.Service], Currency = "USD", Test = true, Enabled = true, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc
            })
            .ToList();
    }
}
