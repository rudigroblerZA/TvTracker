using Catalog.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Services;

/// <summary>
/// Finding shows and people by name: what was typed anywhere in the name, best matches first, and, when little
/// matches, names spelled nearly the same (Postgres trigrams), so a typo or a missing apostrophe still finds the show.
/// Everything compares <see cref="CatalogContext.SearchText"/> (lower case, accents removed), so "elite" finds
/// "Élite", and both kinds of match use the trigram indexes on it.
/// </summary>
public static class TitleSearch
{
    /// <summary>
    /// Shorter terms have too few trigrams to tell a near miss from noise.
    /// </summary>
    public const int MinFuzzyLength = 4;

    /// <summary>
    /// Near-spelled names are looked for only when fewer names than this contain the term: with plenty of real
    /// matches, look-alikes are noise.
    /// </summary>
    public const int FuzzyBelow = 5;

    /// <summary>
    /// The near-spelling cutoff; each catalog connection also carries it as pg_trgm's threshold, for the indexed
    /// <c>&lt;%</c> operator (see CatalogDataExtensions).
    /// </summary>
    public const double MinSimilarity = CatalogContext.NearSpellingThreshold;

    /// <summary>
    /// The term with LIKE's wildcards taken literally (backslash is Postgres's default LIKE escape).
    /// </summary>
    public static string Escape(string term) =>
        term.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>
    /// Shows whose title contains the term.
    /// </summary>
    public static IQueryable<Show> Matching(IQueryable<Show> shows, string term)
    {
        var escaped = Escape(term);
        return shows.Where(s => EF.Functions.Like(CatalogContext.SearchText(s.Title), "%" + CatalogContext.SearchText(escaped) + "%"));
    }

    public static IQueryable<Show> NotMatching(IQueryable<Show> shows, string term)
    {
        var escaped = Escape(term);
        return shows.Where(s => !EF.Functions.Like(CatalogContext.SearchText(s.Title), "%" + CatalogContext.SearchText(escaped) + "%"));
    }

    /// <summary>
    /// People whose name contains the term.
    /// </summary>
    public static IQueryable<Person> Matching(IQueryable<Person> people, string term)
    {
        var escaped = Escape(term);
        return people.Where(p => EF.Functions.Like(CatalogContext.SearchText(p.Name), "%" + CatalogContext.SearchText(escaped) + "%"));
    }

    /// <summary>
    /// Best first: the title that is the term (with or without a leading "The"), then titles with a word starting
    /// with it, then the rest; most popular first within each.
    /// </summary>
    public static IOrderedQueryable<Show> Ranked(IQueryable<Show> matches, string term)
    {
        var escaped = Escape(term);
        return matches
            .OrderBy(s => EF.Functions.Like(CatalogContext.SearchText(s.Title), CatalogContext.SearchText(escaped))
                    || EF.Functions.Like(CatalogContext.SearchText(s.Title), "the " + CatalogContext.SearchText(escaped)) ? 0
                : EF.Functions.Like(CatalogContext.SearchText(s.Title), CatalogContext.SearchText(escaped) + "%")
                    || EF.Functions.Like(CatalogContext.SearchText(s.Title), "% " + CatalogContext.SearchText(escaped) + "%") ? 1
                : 2)
            .ThenByDescending(s => s.Popularity)
            .ThenBy(s => s.Title)
            .ThenBy(s => s.Id);
    }

    /// <summary>
    /// Shows among <paramref name="candidates"/> whose title is spelled nearly like the term. Closest first in broad
    /// bands (a fifth of the scale), and within a band the most popular: among near misses about as close, the
    /// well-known show is nearly always the one meant ("strangr things" is Stranger Things, not 109 Strange Things).
    /// </summary>
    public static async Task<List<Guid>> NearlySpelledAsync(IQueryable<Show> candidates, string term, int take, CancellationToken ct)
    {
        term = term.Trim();
        if (term.Length < MinFuzzyLength || take <= 0)
        {
            return [];
        }
        return await candidates
            .Where(s => EF.Functions.TrigramsAreWordSimilar(CatalogContext.SearchText(term), CatalogContext.SearchText(s.Title)))
            .Select(s => new { s.Id, s.Title, s.Popularity, Similarity = EF.Functions.TrigramsWordSimilarity(CatalogContext.SearchText(term), CatalogContext.SearchText(s.Title)) })
            .Where(x => x.Similarity >= MinSimilarity)
            .OrderByDescending(x => Math.Floor(x.Similarity * 5))
            .ThenByDescending(x => x.Popularity)
            .ThenBy(x => x.Title)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(x => x.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// People whose name is spelled nearly like the term.
    /// </summary>
    public static IQueryable<Person> NearlySpelled(IQueryable<Person> people, string term) =>
        people.Where(p => EF.Functions.TrigramsAreWordSimilar(CatalogContext.SearchText(term), CatalogContext.SearchText(p.Name))
            && EF.Functions.TrigramsWordSimilarity(CatalogContext.SearchText(term), CatalogContext.SearchText(p.Name)) >= MinSimilarity);
}
