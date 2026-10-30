using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="PageUrl">The show's page on Wikiquote, which the UI links and credits (CC BY-SA).</param>
/// <param name="Lines">
/// The <see cref="QuotesApi.OnShowPage"/> shortest exchanges about the whole show if its page has any, else from the first
/// episode with lines, in page order.
/// </param>
/// <param name="FromEpisode">The episode <paramref name="Lines"/> come from, when they come from one.</param>
/// <param name="Episodes">Catalog episodes with lines, and how many each.</param>
public record ShowQuotesDto(Guid ShowId, string PageUrl, IReadOnlyList<QuoteDto> Lines, QuoteEpisodeDto? FromEpisode, int Total,
    IReadOnlyList<QuoteEpisodeDto> Episodes);

public record QuoteEpisodeDto(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, int Count);

/// <param name="Quotes">
/// The <see cref="QuotesApi.PerEpisode"/> shortest, in Wikiquote's order; empty when its page lists nothing for this episode.
/// </param>
/// <param name="More">How many more the page has for the episode.</param>
public record EpisodeQuotesDto(Guid EpisodeId, string PageUrl, IReadOnlyList<QuoteDto> Quotes, int More);

public record QuoteDto(IReadOnlyList<QuoteLineDto> Lines);

/// <param name="Speaker">Null for a stage direction ("[Walter withdraws his life savings]").</param>
public record QuoteLineDto(string? Speaker, string Text);

/// <summary>
/// Memorable lines, from the copy of Wikiquote the sync worker keeps (<c>QuotesService</c>). Lines are stored by
/// Wikiquote's episode numbers and joined to the catalog's episodes here. A show with no Wikiquote page, or none read
/// yet, is 404, so the page leaves the section out. Cached 10 minutes per show; lines change monthly at most.
/// </summary>
public static class QuotesApi
{
    public const int OnShowPage = 3;

    /// <summary>
    /// Lines, not transcripts: an episode page shows this many, and links Wikiquote for the rest (Sherlock's first episode
    /// has 31 exchanges there).
    /// </summary>
    public const int PerEpisode = 6;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private sealed record Stored(int? SeasonNumber, int? EpisodeNumber, List<string> Speakers, List<string> Lines);

    private sealed record StoredEpisode(Guid Id, int SeasonNumber, int EpisodeNumber);

    /// <summary>What's cached per show (as JSON in Redis, so plain lists).</summary>
    private sealed record Cached(string PageUrl, List<Stored> Quotes, List<StoredEpisode> Episodes);

    public static void MapQuotesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/quotes", GetShowQuotesAsync).RequireFeature(FeatureFlags.MemorableLines);
        api.MapGet("/episodes/{id:guid}/quotes", GetEpisodeQuotesAsync).RequireFeature(FeatureFlags.MemorableLines);
    }

    private static async Task<Results<Ok<ShowQuotesDto>, NotFound>> GetShowQuotesAsync(Guid id, CatalogContext db, HybridCache cache,
        CancellationToken ct)
    {
        if (await LoadAsync(db, cache, id, ct) is not { } show)
        {
            return TypedResults.NotFound();
        }

        var catalog = show.Episodes.ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber), e => e.Id);
        var episodes = show.Quotes
            .Where(q => q.SeasonNumber is { } s && q.EpisodeNumber is { } e && catalog.ContainsKey((s, e)))
            .GroupBy(q => (Season: q.SeasonNumber!.Value, Episode: q.EpisodeNumber!.Value))
            .OrderBy(g => g.Key.Season).ThenBy(g => g.Key.Episode)
            .Select(g => new QuoteEpisodeDto(catalog[g.Key], g.Key.Season, g.Key.Episode, g.Count()))
            .ToList();
        var about = show.Quotes.Where(q => q.SeasonNumber is null).ToList();
        // Without lines about the whole show, the first episode's: the least a spoiler a line can be.
        var first = about.Count == 0 ? episodes.FirstOrDefault() : null;
        var lines = first is null
            ? about
            : show.Quotes.Where(q => q.SeasonNumber == first.SeasonNumber && q.EpisodeNumber == first.EpisodeNumber).ToList();
        return TypedResults.Ok(new ShowQuotesDto(id, show.PageUrl, Shortest(lines, OnShowPage), first,
            about.Count + episodes.Sum(e => e.Count), episodes));
    }

    private static async Task<Results<Ok<EpisodeQuotesDto>, NotFound>> GetEpisodeQuotesAsync(Guid id, CatalogContext db, HybridCache cache,
        CancellationToken ct)
    {
        var episode = await db.Episodes.AsNoTracking().Where(e => e.Id == id)
            .Select(e => new { e.ShowId, e.SeasonNumber, e.EpisodeNumber })
            .SingleOrDefaultAsync(ct);
        if (episode is null || await LoadAsync(db, cache, episode.ShowId, ct) is not { } show)
        {
            return TypedResults.NotFound();
        }

        var quotes = show.Quotes.Where(q => q.SeasonNumber == episode.SeasonNumber && q.EpisodeNumber == episode.EpisodeNumber).ToList();
        return TypedResults.Ok(new EpisodeQuotesDto(id, show.PageUrl, Shortest(quotes, PerEpisode), Math.Max(0, quotes.Count - PerEpisode)));
    }

    private static async Task<Cached?> LoadAsync(CatalogContext db, HybridCache cache, Guid showId, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"quotes:{showId}", async token =>
        {
            var source = await db.QuoteSources.AsNoTracking()
                .Where(q => q.ShowId == showId && q.Page != null && q.FetchedAtUtc != null)
                .Select(q => q.Page)
                .SingleOrDefaultAsync(token);
            if (source is null)
            {
                return null;
            }
            var quotes = await db.Quotes.AsNoTracking().Where(q => q.ShowId == showId)
                .OrderBy(q => q.Position)
                .Select(q => new Stored(q.SeasonNumber, q.EpisodeNumber, q.Speakers, q.Lines))
                .ToListAsync(token);
            if (quotes.Count == 0)
            {
                return null;
            }
            var episodes = await db.Episodes.AsNoTracking().Where(e => e.ShowId == showId && e.SeasonNumber > 0)
                .Select(e => new StoredEpisode(e.Id, e.SeasonNumber, e.EpisodeNumber))
                .ToListAsync(token);
            // Same-night crossovers can repeat a number; the first keeps it.
            return new Cached(ShowQuoteSource.PageUrl(source), quotes, episodes.DistinctBy(e => (e.SeasonNumber, e.EpisodeNumber)).ToList());
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);

    /// <summary>The shortest few, in page order: a page has room for lines, not scenes.</summary>
    private static List<QuoteDto> Shortest(IEnumerable<Stored> quotes, int count) =>
        quotes.Select((q, i) => (Quote: q, Index: i))
            .OrderBy(x => x.Quote.Lines.Sum(l => l.Length)).Take(count)
            .OrderBy(x => x.Index).Select(x => ToDto(x.Quote)).ToList();

    private static QuoteDto ToDto(Stored quote) =>
        new(quote.Lines.Select((text, i) => new QuoteLineDto(quote.Speakers.ElementAtOrDefault(i) is { Length: > 0 } s ? s : null, text)).ToList());
}
