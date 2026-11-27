using Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Search;

/// <param name="Shows">This page's shows, in order.</param>
/// <param name="Total">How many shows the search found in all.</param>
/// <param name="TitleCount">
/// How many of them were found by title (containing the term, or spelled nearly like it); the rest are similar in
/// meaning. Null when nothing was searched for.
/// </param>
public sealed record ShowSearchPage(IReadOnlyList<Show> Shows, int Total, int? TitleCount);

/// <summary>
/// Search over a set of shows, a page at a time. Three kinds of match, always in this order:
/// <list type="number">
/// <item>titles that contain the term, best match first (<see cref="TitleSearch.Ranked"/>);</item>
/// <item>when fewer than <see cref="TitleSearch.FuzzyBelow"/> do, up to <see cref="MaxNearlySpelled"/> titles spelled
/// nearly like it;</item>
/// <item>up to <see cref="SemanticSearch.MaxResults"/> shows similar in meaning, closest first.</item>
/// </list>
/// The order is a rule, not a score: a typed title is nearly always what was wanted, so a title match comes before a
/// match by meaning however close that one is. Without a term, the shows come most popular first.
/// </summary>
public sealed class ShowSearch(SemanticSearch semantic)
{
    public const int MaxNearlySpelled = 12;

    /// <summary>
    /// How many titles <see cref="SuggestAsync"/> offers.
    /// </summary>
    public const int MaxSuggestions = 8;

    /// <param name="among">The shows to search: the catalog, or what the caller's filters leave of it.</param>
    /// <param name="page">From 1.</param>
    public async Task<ShowSearchPage> PageAsync(CatalogContext db, IQueryable<Show> among, string? term, int page, int pageSize, CancellationToken ct)
    {
        var searching = !string.IsNullOrWhiteSpace(term);
        var titleMatches = searching ? TitleSearch.Matching(among, term!) : among;
        var ordered = searching
            ? TitleSearch.Ranked(titleMatches, term!)
            : titleMatches.OrderByDescending(s => s.Popularity).ThenBy(s => s.Title);

        // Title matches fill the first pages; near-spelled titles and then the similar shows come after the last of them.
        var titleTotal = await titleMatches.CountAsync(ct);
        List<Guid> after = [];
        var nearlySpelled = 0;
        if (searching)
        {
            var others = TitleSearch.NotMatching(among, term!);
            if (titleTotal < TitleSearch.FuzzyBelow && TitleSearch.AllowsNearSpelling(term!))
            {
                after = await TitleSearch.NearlySpelled(others, term!).Select(s => s.Id).Take(MaxNearlySpelled).ToListAsync(ct);
                nearlySpelled = after.Count;
            }
            after.AddRange((await semantic.FindSimilarAsync(others, db, term!, ct)).Except(after));
        }

        var skip = (page - 1) * pageSize;
        var shows = skip < titleTotal ? await ordered.Skip(skip).Take(pageSize).ToListAsync(ct) : [];
        shows.AddRange(await InOrderAsync(db, after.Skip(Math.Max(0, skip - titleTotal)).Take(pageSize - shows.Count).ToList(), ct));

        return new ShowSearchPage(shows, titleTotal + after.Count, searching ? titleTotal + nearlySpelled : null);
    }

    /// <summary>
    /// Up to <see cref="MaxSuggestions"/> shows whose title contains the term, best match first, for suggesting as
    /// someone types; when hardly any do, titles spelled nearly like it. Never by meaning, so it's cheap enough for
    /// every keystroke. Empty for a term under two characters.
    /// </summary>
    public static async Task<List<Show>> SuggestAsync(IQueryable<Show> among, string? term, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
        {
            return [];
        }

        var shows = await TitleSearch.Ranked(TitleSearch.Matching(among, term), term).Take(MaxSuggestions).ToListAsync(ct);
        if (shows.Count < TitleSearch.FuzzyBelow && TitleSearch.AllowsNearSpelling(term))
        {
            shows.AddRange(await TitleSearch.NearlySpelled(TitleSearch.NotMatching(among, term), term).Take(MaxSuggestions - shows.Count).ToListAsync(ct));
        }
        return shows;
    }

    private static async Task<List<Show>> InOrderAsync(CatalogContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var found = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        return ids.Where(found.ContainsKey).Select(id => found[id]).ToList();
    }
}

public static class CatalogSearchExtensions
{
    /// <summary>
    /// Registers <see cref="ShowSearch"/> and <see cref="SemanticSearch"/>. The embedding model is registered
    /// separately (Catalog.Embeddings' AddLocalEmbeddings).
    /// </summary>
    public static IServiceCollection AddCatalogSearch(this IServiceCollection services) =>
        services.AddSingleton<SemanticSearch>().AddSingleton<ShowSearch>();
}
