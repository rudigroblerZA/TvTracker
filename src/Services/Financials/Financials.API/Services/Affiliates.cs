using System.Security.Cryptography;
using System.Text;
using Financials.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Financials.API.Services;

/// <summary>
/// The affiliate programs the app has joined (<c>Affiliates:Programs</c>); none by default.
/// </summary>
public sealed class AffiliateOptions
{
    public List<AffiliateProgram> Programs { get; set; } = [];
}

/// <summary>
/// Affiliate links: which program covers a click, and the tracked link it becomes. A service with no program gets its
/// plain link, still counted, so the clicks a service would be worth are known before there's a deal.
/// </summary>
public static class AffiliateNetworks
{
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
                Services = [AffiliateLinks.AnyService],
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
/// and a change in Settings applies almost at once (<see cref="ChangedAsync"/> makes it at once for the admin who made it).
/// </summary>
public sealed class AffiliatePrograms(FinancialsContext db, IOptionsMonitor<AffiliateOptions> options, IConfiguration configuration, IMemoryCache cache,
    IIntegrationEventOutbox outbox, TimeProvider time)
{
    private const string CacheKey = nameof(AffiliatePrograms);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<AffiliateProgram>> AllAsync(CancellationToken ct)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<AffiliateProgram>? programs))
        {
            programs = Merge(await db.AffiliatePrograms.AsNoTracking().ToListAsync(ct), options.CurrentValue.Programs,
                AffiliateNetworks.TestNetwork(configuration));
            cache.Set(CacheKey, programs, CacheFor);
        }
        return programs!;
    }

    /// <summary>
    /// After a change: forgets the cached programs and tells Catalog.API, which builds the links, what's in force now.
    /// </summary>
    public async Task ChangedAsync(CancellationToken ct)
    {
        cache.Remove(CacheKey);
        await PublishAsync(ct);
    }

    /// <summary>
    /// Publishes the programs in force (see <see cref="AffiliateProgramsChangedIntegrationEvent"/>).
    /// </summary>
    public async Task PublishAsync(CancellationToken ct)
    {
        var programs = (await AllAsync(ct)).Select(AffiliateProgramLink.From).ToList();
        outbox.Add(new AffiliateProgramsChangedIntegrationEvent(programs, time.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
    }

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
                Name = p.Name,
                Services = [p.Service],
                Currency = "USD",
                Test = true,
                Enabled = true,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            })
            .ToList();
    }
}

/// <summary>
/// Keeps Catalog.API's copy of the programs fresh: once shortly after starting (a new deployment, or a Catalog whose copy
/// was lost), then hourly. An admin's change is published as it's saved (<see cref="AffiliatePrograms.ChangedAsync"/>).
/// </summary>
public sealed class AffiliateProgramsPublisher(IServiceScopeFactory scopes, TimeProvider time, ILogger<AffiliateProgramsPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), time, stoppingToken);
        using var timer = new PeriodicTimer(Every, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AffiliatePrograms>().PublishAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Publishing the affiliate programs failed; trying again in an hour");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
