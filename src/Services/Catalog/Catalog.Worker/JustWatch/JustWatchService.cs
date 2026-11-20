using Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Catalog.Worker.JustWatch;

/// <summary>
/// Where to watch, from JustWatch. Each round asks about the most popular shows not asked about within
/// <see cref="JustWatchOptions.RefreshDays"/>, country by country, and replaces that country's where-to-watch rows
/// with JustWatch's.
/// <list type="bullet">
/// <item><b>Off without a token.</b> The catalog's own guess (the provider's network or web channel) stays as it is.</item>
/// <item><b>Which show:</b> by IMDb id when the provider gave one; else by title and premiere year, taken only when
/// the title and year that come back are the show's.</item>
/// <item><b>Known to JustWatch:</b> the country's rows are JustWatch's alone, even when it lists nothing there: the
/// show isn't streaming in that country, and the catalog's guess is dropped.</item>
/// <item><b>Not known:</b> JustWatch's rows for the country go, and the catalog's guess returns on the show's next refresh.</item>
/// <item><b>Gently:</b> one request at a time at <see cref="JustWatchOptions.RequestsPerMinute"/>; a failure ends the
/// round, and a refused token stops the asking for <see cref="RefusedWait"/>.</item>
/// </list>
/// </summary>
public sealed class JustWatchService(IServiceScopeFactory scopeFactory, JustWatchClient client, IOptions<JustWatchOptions> options,
    IConfiguration configuration, TimeProvider time, ILogger<JustWatchService> logger) : BackgroundService
{
    private const int Batch = 25;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefusedWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReferenceFor = TimeSpan.FromDays(1);

    // JustWatch's countries and each locale's services, kept for a day. Only this service's loop touches them.
    private IReadOnlyList<JustWatchCountry> _countries = [];
    private readonly Dictionary<string, IReadOnlyDictionary<long, string>> _providers = [];
    private DateTimeOffset _referenceAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Token))
        {
            logger.LogInformation("JustWatch: off, since there is no partner token (JustWatch:Token)");
            return;
        }
        var regions = Regions();
        logger.LogInformation("JustWatch: on for {Regions}", string.Join(", ", regions));
        await Task.Delay(FirstDelay, time, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = Round;
            try
            {
                foreach (var region in regions)
                {
                    await AskAsync(region, stoppingToken);
                }
            }
            catch (JustWatchRefusedException ex)
            {
                wait = RefusedWait;
                logger.LogError("JustWatch: {Reason} Check JustWatch:Token; trying again in {Wait}", ex.Message, wait);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "JustWatch: the round failed; trying again in {Round}", Round);
            }
            await Task.Delay(wait, time, stoppingToken);
        }
    }

    private List<string> Regions()
    {
        var own = options.Value.Regions;
        var regions = own.Count > 0 ? own : configuration.GetSection("TvMaze:WatchRegions").Get<List<string>>() ?? ["US", "GB", "ZA"];
        return regions.Where(r => r is { Length: 2 }).Select(r => r.ToUpperInvariant()).Distinct().ToList();
    }

    /// <summary>
    /// Asks about one batch of shows in one country.
    /// </summary>
    private async Task AskAsync(string region, CancellationToken ct)
    {
        if (time.GetUtcNow() - _referenceAt > ReferenceFor)
        {
            _countries = await client.CountriesAsync(ct);
            _providers.Clear();
            _referenceAt = time.GetUtcNow();
        }
        if (JustWatchMapping.LocaleFor(region, _countries) is not { } locale)
        {
            return;
        }
        if (!_providers.TryGetValue(locale, out var providers))
        {
            providers = (await client.ProvidersAsync(locale, ct)).Where(p => p.ClearName is not null)
                .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().ClearName!);
            _providers[locale] = providers;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var staleBefore = time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, options.Value.RefreshDays));
        var due = await db.Shows.AsNoTracking()
            .Where(s => !db.JustWatchListings.Any(l => l.ShowId == s.Id && l.Region == region && l.CheckedAtUtc >= staleBefore))
            .OrderByDescending(s => s.Popularity)
            .Select(s => new { s.Id, s.Title, s.ImdbId, s.FirstAirDate })
            .Take(Batch)
            .ToListAsync(ct);

        var pause = TimeSpan.FromMinutes(1.0 / Math.Clamp(options.Value.RequestsPerMinute, 1, 600));
        var found = 0;
        foreach (var show in due)
        {
            JustWatchTitle? title = null;
            if (show.ImdbId is { Length: > 0 } imdb)
            {
                title = await client.ShowByImdbAsync(locale, imdb, ct);
            }
            else if (show.FirstAirDate is { } premiered
                     && await client.ShowByTitleAsync(locale, show.Title, premiered.Year, ct) is { } byTitle
                     && JustWatchMapping.IsSameShow(byTitle, show.Title, premiered.Year))
            {
                title = byTitle;
            }
            await ApplyAsync(db, show.Id, region, title is null ? null : JustWatchMapping.Options(title, providers),
                title is null ? null : JustWatchMapping.PathOf(title), time.GetUtcNow().UtcDateTime, ct);
            found += title is null ? 0 : 1;
            await Task.Delay(pause, time, ct);
        }
        if (due.Count > 0)
        {
            logger.LogInformation("JustWatch: asked about {Shows} shows in {Region}; it knows {Found}", due.Count, region, found);
        }
    }

    /// <summary>
    /// Stores one answer: the listing, and the country's where-to-watch rows. <paramref name="offers"/> is null when
    /// JustWatch doesn't know the show.
    /// </summary>
    internal static async Task ApplyAsync(CatalogContext db, Guid showId, string region, IReadOnlyList<JustWatchOption>? offers, string? path,
        DateTime nowUtc, CancellationToken ct)
    {
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var listing = await db.JustWatchListings.FirstOrDefaultAsync(l => l.ShowId == showId && l.Region == region, ct);
            if (listing is null)
            {
                listing = new JustWatchListing { ShowId = showId, Region = region };
                db.JustWatchListings.Add(listing);
            }
            (listing.Found, listing.Path, listing.CheckedAtUtc) = (offers is not null, path, nowUtc);

            // Known: every row of the country goes, the catalog's guess included. Unknown: only JustWatch's own.
            await db.WatchProviders
                .Where(w => w.ShowId == showId && w.Region == region && (offers != null || w.Source == WatchSource.JustWatch))
                .ExecuteDeleteAsync(ct);
            db.WatchProviders.AddRange((offers ?? []).Select(o => new WatchProvider
            {
                Id = Guid.CreateVersion7(),
                ShowId = showId,
                Region = region,
                ProviderName = o.ProviderName,
                Type = o.Type,
                Url = o.Url,
                Source = WatchSource.JustWatch
            }));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }
}
