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
    /// The first valid program covering the service in the region, if any.
    /// </summary>
    public static AffiliateProgram? For(IEnumerable<AffiliateProgram> programs, string service, string region) =>
        programs.FirstOrDefault(p => IsValid(p)
            && (p.Services.Contains(service, StringComparer.OrdinalIgnoreCase) || p.Services.Contains(AnyService))
            && (p.Regions.Count == 0 || p.Regions.Contains(region, StringComparer.OrdinalIgnoreCase)));

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
    /// The development test network (<c>Affiliates:TestNetwork:Url</c> and <c>:Key</c>, set by the AppHost with
    /// <c>Affiliates:TestNetwork=true</c>): every service, every region, its tracked link a page in the web app that can
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
/// App admins: Keycloak usernames in <c>Admin:Users</c>, as Identity's flags page reads them.
/// </summary>
/// <summary>
/// Every affiliate program in force, in the order a click tries them: an admin's from Settings, then the
/// configuration's, then the test network's catch-all. Kept a few seconds, so a click doesn't read the table each time
/// and a change in Settings applies almost at once (<see cref="Forget"/> makes it at once for the admin who made it).
/// </summary>
public sealed class AffiliatePrograms(CatalogContext db, IOptionsMonitor<AffiliateOptions> options, IConfiguration configuration, IMemoryCache cache)
{
    private const string CacheKey = nameof(AffiliatePrograms);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<AffiliateProgram>> AllAsync(CancellationToken ct)
    {
        if (!cache.TryGetValue(CacheKey, out List<AffiliateProgramRecord>? records))
        {
            records = await db.AffiliatePrograms.AsNoTracking().ToListAsync(ct);
            cache.Set(CacheKey, records, CacheFor);
        }
        return Merge(records!, options.CurrentValue.Programs, AffiliateLinks.TestNetwork(configuration));
    }

    public void Forget() => cache.Remove(CacheKey);

    /// <summary>
    /// Settings' programs that are switched on (a test one with the test network's link and key, or unusable without
    /// one), then the configuration's that Settings doesn't name, then the test network's catch-all.
    /// </summary>
    public static IReadOnlyList<AffiliateProgram> Merge(IEnumerable<AffiliateProgramRecord> records, IEnumerable<AffiliateProgram> configured,
        AffiliateProgram? testNetwork)
    {
        var fromSettings = records.Where(r => r.Enabled).OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => new AffiliateProgram
        {
            Name = r.Name,
            Services = r.Services,
            Regions = r.Regions,
            Template = r.Test ? testNetwork?.Template ?? "" : r.Template,
            PostbackKey = r.Test ? testNetwork?.PostbackKey : r.PostbackKey,
            Currency = r.Currency,
            Source = AffiliateSources.Settings,
            Test = r.Test
        }).ToList();
        var named = records.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fromConfiguration = configured.Where(p => p.Source == AffiliateSources.Configuration && !named.Contains(p.Name));
        return [.. fromSettings, .. fromConfiguration, .. testNetwork is null ? [] : new[] { testNetwork }];
    }
}

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
    /// The starter programs not in <paramref name="existing"/> yet, as records to add.
    /// </summary>
    public static List<AffiliateProgramRecord> Missing(IEnumerable<string> existing, DateTime nowUtc)
    {
        var have = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(p => !have.Contains(p.Name))
            .Select(p => new AffiliateProgramRecord
            {
                Name = p.Name, Services = [p.Service], Currency = "USD", Test = true, Enabled = true, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc
            })
            .ToList();
    }
}

/// <summary>
/// In development with the test network running, an empty programs table starts with <see cref="StarterPrograms"/>.
/// Only when empty: programs an admin removed don't come back while any other is left.
/// </summary>
public sealed class AffiliateProgramSeeder(IServiceScopeFactory scopes, IHostEnvironment environment, IConfiguration configuration, TimeProvider time,
    ILogger<AffiliateProgramSeeder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment() || AffiliateLinks.TestNetwork(configuration) is null)
        {
            return;
        }
        // The table may not be there yet: the database is migrated as the service starts.
        for (var attempt = 0; attempt < 20 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
                if (!await db.AffiliatePrograms.AnyAsync(stoppingToken))
                {
                    db.AffiliatePrograms.AddRange(StarterPrograms.Missing([], time.GetUtcNow().UtcDateTime));
                    await db.SaveChangesAsync(stoppingToken);
                    logger.LogInformation("Affiliate programs: added {Count} test programs to start with", StarterPrograms.All.Count);
                }
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Affiliate programs: the table isn't ready yet; trying again");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
