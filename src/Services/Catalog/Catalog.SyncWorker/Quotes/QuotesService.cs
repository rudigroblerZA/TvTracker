using Catalog.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.SyncWorker.Quotes;

/// <summary>
/// Memorable lines from Wikiquote (CC BY-SA). Each round looks up one batch of shows' pages on Wikidata (by TVmaze
/// id, monthly) and reads one show's page and season pages, most popular first, refreshed every
/// <see cref="RefreshAfter"/>. The show's lines are then replaced in one transaction.
/// <list type="bullet">
/// <item><b>Gently:</b> Wikimedia has no published limit for wikitext reads but rate-limits shared addresses hard, so
/// pages are read one at a time, <see cref="BetweenPages"/> apart, and a 429 is waited out as asked (up to
/// <see cref="MaxWait"/>). Nothing here touches TVmaze, so its shared limiter isn't involved.</item>
/// <item><b>Per show:</b> a show that fails (a page that can't be read, a busy answer three times) is skipped for
/// <see cref="FailureRetry"/> and keeps its lines; the loop carries on.</item>
/// <item><b>Matching episodes:</b> lines are stored by the episode numbers in Wikiquote's headings, and joined to the
/// catalog's episodes when read, so a line still finds its episode after a refresh re-creates it.</item>
/// </list>
/// </summary>
public sealed class QuotesService(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<QuotesService> logger) : BackgroundService
{
    private const string Provider = "tvmaze";
    private const int LookupBatch = 100;
    private const int Attempts = 3;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BetweenPages = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LookupAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(60);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromHours(6);

    // Shows that failed, and when to try them again. Only this service's loop touches it.
    private readonly Dictionary<Guid, DateTimeOffset> _failed = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstDelay, time, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await LookUpAsync(stoppingToken);
                await ReadNextAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Memorable lines: the round failed; trying again in {Round}", Round);
            }
            await Task.Delay(Round, time, stoppingToken);
        }
    }

    /// <summary>Finds the Wikiquote pages of one batch of shows never looked up, or not for a month.</summary>
    private async Task LookUpAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var staleBefore = now - LookupAfter;
        var due = await db.Shows.AsNoTracking()
            .Where(s => s.Source == Provider)
            .Where(s => !db.QuoteSources.Any(q => q.ShowId == s.Id && q.CheckedAtUtc >= staleBefore))
            .OrderByDescending(s => s.Popularity)
            .Select(s => new { s.Id, s.ExternalId })
            .Take(LookupBatch)
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return;
        }

        var pages = await WithRetryAsync(() => WikiquoteClient.FindPagesAsync(due.Select(s => s.ExternalId), ct), ct);
        var ids = due.Select(s => s.Id).ToList();
        var existing = await db.QuoteSources.Where(q => ids.Contains(q.ShowId)).ToDictionaryAsync(q => q.ShowId, ct);
        foreach (var show in due)
        {
            if (!existing.TryGetValue(show.Id, out var source))
            {
                source = new ShowQuoteSource { ShowId = show.Id };
                db.QuoteSources.Add(source);
            }
            var page = pages.GetValueOrDefault(show.ExternalId);
            if (page != source.Page)
            {
                // A new or moved page is read again at once.
                source.FetchedAtUtc = null;
            }
            source.Page = page;
            source.CheckedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Memorable lines: looked up {Shows} shows on Wikidata, {Found} have a Wikiquote page", due.Count, pages.Count);
    }

    /// <summary>Reads the most popular show whose lines are missing or stale, and replaces them.</summary>
    private async Task ReadNextAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var now = time.GetUtcNow();
        var staleBefore = now.UtcDateTime - RefreshAfter;
        var skip = _failed.Where(f => f.Value > now).Select(f => f.Key).ToList();
        var next = await db.QuoteSources
            .Where(q => q.Page != null && (q.FetchedAtUtc == null || q.FetchedAtUtc < staleBefore) && !skip.Contains(q.ShowId))
            .OrderBy(q => q.FetchedAtUtc != null)
            .ThenByDescending(q => db.Shows.Where(s => s.Id == q.ShowId).Select(s => s.Popularity).FirstOrDefault())
            .FirstOrDefaultAsync(ct);
        if (next is null)
        {
            return;
        }

        List<ParsedQuote> parsed;
        try
        {
            parsed = await ReadPagesAsync(next.Page!, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _failed[next.ShowId] = now + FailureRetry;
            logger.LogWarning(ex, "Memorable lines: couldn't read {Page}; trying again in {Retry}", next.Page, FailureRetry);
            return;
        }

        var quotes = parsed.Select((q, i) => new Quote
        {
            Id = Guid.CreateVersion7(), ShowId = next.ShowId, SeasonNumber = q.Season, EpisodeNumber = q.Episode, Position = i,
            Speakers = q.Lines.Select(l => l.Speaker).ToList(), Lines = q.Lines.Select(l => l.Text).ToList()
        }).ToList();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Quotes.Where(q => q.ShowId == next.ShowId).ExecuteDeleteAsync(ct);
            db.Quotes.AddRange(quotes);
            next.FetchedAtUtc = now.UtcDateTime;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
        logger.LogInformation("Memorable lines: {Quotes} from {Page} ({Episodes} episodes)", quotes.Count, next.Page,
            quotes.Where(q => q.EpisodeNumber is not null).Select(q => (q.SeasonNumber, q.EpisodeNumber)).Distinct().Count());
    }

    /// <summary>The show's page and every season page it links to, parsed in page order.</summary>
    private async Task<List<ParsedQuote>> ReadPagesAsync(string page, CancellationToken ct)
    {
        var main = await WithRetryAsync(() => WikiquoteClient.GetWikitextAsync(page, ct), ct);
        if (main is null)
        {
            return [];
        }

        var quotes = WikiquoteParser.Parse(main).ToList();
        foreach (var season in WikiquoteParser.SeasonPages(main, page))
        {
            await Task.Delay(BetweenPages, time, ct);
            if (await WithRetryAsync(() => WikiquoteClient.GetWikitextAsync(season, ct), ct) is { } text)
            {
                // Episodes only (lines about the whole show come from its own page), and one that an earlier page already
                // had keeps that page's lines.
                var seen = quotes.Where(q => q.Episode is not null).Select(q => (q.Season, q.Episode)).ToHashSet();
                quotes.AddRange(WikiquoteParser.Parse(text).Where(q => q.Episode is not null && !seen.Contains((q.Season, q.Episode))));
            }
        }
        return quotes;
    }

    /// <summary>A Wikimedia call, waiting out a 429's Retry-After (up to <see cref="MaxWait"/>) between tries.</summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (WikimediaBusyException busy) when (attempt < Attempts)
            {
                var wait = busy.RetryAfter is { } after && after < MaxWait ? after : TimeSpan.FromMinutes(attempt * 2);
                logger.LogInformation("Memorable lines: Wikimedia is busy; waiting {Seconds:0} s", wait.TotalSeconds);
                await Task.Delay(wait, time, ct);
            }
        }
    }
}
