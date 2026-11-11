using TvTracker;
using Catalog.Data;
using Catalog.SyncWorker.Providers.Wikidata;
using Microsoft.EntityFrameworkCore;
using static Catalog.SyncWorker.Providers.Wikidata.WikidataMapping;

namespace Catalog.SyncWorker.Supplement;

/// <summary>
/// Fills gaps in the catalog from Wikidata: for shows the provider gave no overview, network, premiere date or poster,
/// it finds the show's Wikidata item by the provider's id and keeps what Wikidata has (<see cref="ShowSupplement"/>).
/// The synchronizer puts those values in on every sync while the provider's are empty, so this job only stores them
/// and marks the show for a refresh, which publishes the change like any other.
/// <para>
/// A round is <see cref="Batch"/> shows, most popular first; a show is asked about again after <see cref="LookupAfter"/>.
/// On its own it does a round every <see cref="Round"/> while there's work; asked to run now
/// (<c>tvtracker-import wikidata run fill</c>), it works through everything due. Requests go through
/// <see cref="WikidataSparql"/>, which spaces them and waits out Wikimedia's 429s.
/// </para>
/// </summary>
public sealed class WikidataSupplementService(IServiceScopeFactory scopeFactory, WikidataSparql wikidata, TimeProvider time,
    IConfiguration configuration, [FromKeyedServices(WikidataJobs.Fill)] JobRun run, ILogger<WikidataSupplementService> logger)
    : BackgroundService
{
    private const int Batch = 25;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Idle = TimeSpan.FromHours(6);
    private static readonly TimeSpan LookupAfter = TimeSpan.FromDays(30);

    private sealed record Found(string ExternalId, string Item, string? Description, string? Article, DateOnly? Start, string? Image, string? Network);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var source = CatalogSources.Configured(configuration);
        if (CatalogSupplements.IdProperty(source) is not { } property)
        {
            run.TurnOff($"Off: Wikidata has no id to find {source}'s shows by.");
            logger.LogInformation("Wikidata gap-fill: off, since Wikidata has no id to find {Source}'s shows by", source);
            return;
        }
        await run.SleepAsync(FirstDelay, time, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = Idle;
            run.RoundStarted();
            try
            {
                var (looked, due) = await LookUpAsync(source, property, stoppingToken);
                run.RoundEnded(looked, due, time.GetUtcNow().UtcDateTime);
                next = due > 0 ? Round : Idle;
            }
            // Not only "not an OperationCanceledException": a request that times out is a TaskCanceledException too, and
            // one escaping here would stop the whole service.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Wikidata gap-fill: the round failed; trying again in {Round}", Round);
                run.RoundEnded(0, null, time.GetUtcNow().UtcDateTime, ex.Message);
                next = Round;
            }
            // Asked to run now: straight on to the next batch.
            if (!run.Requested)
            {
                await run.SleepAsync(next, time, stoppingToken);
            }
        }
    }

    /// <summary>
    /// One batch: how many shows were asked about, and how many are still due after it.
    /// </summary>
    private async Task<(int Looked, int Due)> LookUpAsync(string source, string property, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var staleBefore = now - LookupAfter;
        var incomplete = db.Shows.AsNoTracking()
            .Where(s => s.Source == source
                && (s.Overview == null || s.Overview == "" || s.Network == null || s.FirstAirDate == null || s.PosterUrl == null)
                && !db.Supplements.Any(l => l.ShowId == s.Id && l.CheckedAtUtc >= staleBefore));
        var due = await incomplete
            .OrderByDescending(s => s.Popularity)
            .Select(s => new { s.Id, s.ExternalId, s.Overview, s.Network, s.FirstAirDate, s.PosterUrl })
            .Take(Batch)
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return (0, 0);
        }

        var found = await QueryAsync(property, due.Select(s => s.ExternalId), ct);
        var supplements = new List<ShowSupplement>();
        var helped = new List<Guid>();
        foreach (var show in due)
        {
            var supplement = new ShowSupplement { ShowId = show.Id, CheckedAtUtc = now };
            if (found.GetValueOrDefault(show.ExternalId) is { } item)
            {
                supplement.Item = item.Item;
                supplement.Network = item.Network is { Length: > 0 and <= 128 } network ? network : null;
                supplement.FirstAirDate = item.Start;
                supplement.PosterUrl = Image(item.Image, 680);
                // The article is one more request, so only for a show without an overview.
                if (string.IsNullOrWhiteSpace(show.Overview))
                {
                    supplement.Overview = (item.Article is { } article ? await SummaryOrNullAsync(article, ct) : null)
                        ?? (string.IsNullOrWhiteSpace(item.Description) ? null : char.ToUpperInvariant(item.Description[0]) + item.Description[1..] + ".");
                }
            }
            supplements.Add(supplement);
            if (CatalogSupplements.Helps(supplement, show.Overview, show.Network, show.FirstAirDate, show.PosterUrl))
            {
                helped.Add(show.Id);
            }
        }

        var ids = due.Select(s => s.Id).ToList();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Supplements.Where(s => ids.Contains(s.ShowId)).ExecuteDeleteAsync(ct);
            db.Supplements.AddRange(supplements);
            await db.SaveChangesAsync(ct);
            // The synchronizer fills the fields in, and tells the other services, on the show's next sync: now.
            await db.Shows.Where(s => helped.Contains(s.Id) && s.NextRefreshAtUtc > now)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRefreshAtUtc, now), ct);
            await transaction.CommitAsync(ct);
        });
        logger.LogInformation("Wikidata gap-fill: asked about {Shows} shows, {Found} on Wikidata, {Helped} with something to fill in",
            due.Count, found.Count, helped.Count);
        return (due.Count, await incomplete.CountAsync(ct));
    }

    private async Task<Dictionary<string, Found>> QueryAsync(string property, IEnumerable<string> externalIds, CancellationToken ct)
    {
        var values = string.Join(' ', externalIds.Select(WikidataSparql.Literal));
        var rows = await wikidata.QueryAsync($$"""
            SELECT ?id ?item ?description ?article (MIN(?start) AS ?first) (MIN(?image) AS ?img) (MIN(?networkName) AS ?network) WHERE {
              VALUES ?id { {{values}} }
              ?item wdt:{{property}} ?id .
              OPTIONAL { ?item schema:description ?description FILTER(LANG(?description) = "en") }
              OPTIONAL { ?article schema:about ?item ; schema:isPartOf <https://en.wikipedia.org/> }
              # The start time, else the publication date: many series items have only the latter.
              OPTIONAL { ?item wdt:P580 ?started }
              OPTIONAL { ?item wdt:P577 ?published }
              BIND(COALESCE(?started, ?published) AS ?start)
              OPTIONAL { ?item wdt:P18 ?image }
              OPTIONAL { ?item wdt:P449 ?net . ?net rdfs:label ?networkName FILTER(LANG(?networkName) = "en") }
            } GROUP BY ?id ?item ?description ?article
            """, ct);
        return rows
            .Select(r => new Found(r["id"], Id(r["item"]), r.GetValueOrDefault("description"), r.GetValueOrDefault("article"),
                Date(r.GetValueOrDefault("first")), r.GetValueOrDefault("img"), r.GetValueOrDefault("network")))
            .GroupBy(f => f.ExternalId)
            // An id can sit on two items (a series and a duplicate): the older item, so a refresh reads the same.
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Item.Length).ThenBy(f => f.Item, StringComparer.Ordinal).First());
    }

    private async Task<string?> SummaryOrNullAsync(string article, CancellationToken ct)
    {
        try
        {
            return await wikidata.WikipediaSummaryAsync(article, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation(ex, "Wikidata gap-fill: no Wikipedia summary for {Article}; using Wikidata's description", article);
            return null;
        }
    }
}
