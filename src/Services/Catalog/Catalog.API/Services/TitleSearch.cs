using Catalog.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Services;

/// <summary>
/// Finding shows and people by name: what was typed anywhere in the name, best matches first, and, when little
/// matches, names spelled nearly the same (Postgres trigrams), so a typo or a missing apostrophe still finds the show.
/// </summary>
public static class TitleSearch
{
    /// <summary>
    /// Shorter terms have too few trigrams to tell a near miss from noise.
    /// </summary>
    public const int MinFuzzyLength = 4;

    /// <summary>
    /// Near-spelled titles are looked for only when fewer titles than this contain the term: with plenty of real
    /// matches, look-alikes are noise.
    /// </summary>
    public const int FuzzyBelow = 5;

    /// <summary>
    /// How alike the term and the closest stretch of a name must be (trigram word similarity, 0 to 1), accents aside.
    /// Measured on the catalog: "breakng bad" against "Breaking Bad" scores 0.64, "severence" against "Severance" 0.54,
    /// and the look-alikes to keep out sit at 0.50 ("pokemon" against "Poker Face").
    /// </summary>
    public const double MinSimilarity = 0.52;

    private const double SameButForAccents = 0.95;

    private const string EscapeCharacter = "\\";

    /// <summary>
    /// The term as a LIKE pattern matching it anywhere, with the wildcards someone typed taken literally.
    /// </summary>
    public static string Contains(string term) => $"%{Escape(term)}%";

    public static string Escape(string term) =>
        term.Trim().Replace(EscapeCharacter, @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>
    /// Shows whose title contains the term.
    /// </summary>
    public static IQueryable<Show> Matching(IQueryable<Show> shows, string term)
    {
        var contains = Contains(term);
        return shows.Where(s => EF.Functions.ILike(s.Title, contains, EscapeCharacter));
    }

    public static IQueryable<Show> NotMatching(IQueryable<Show> shows, string term)
    {
        var contains = Contains(term);
        return shows.Where(s => !EF.Functions.ILike(s.Title, contains, EscapeCharacter));
    }

    /// <summary>
    /// Best first: the title that is the term (with or without a leading "The"), then titles with a word starting
    /// with it, then the rest; most popular first within each.
    /// </summary>
    public static IOrderedQueryable<Show> Ranked(IQueryable<Show> matches, string term)
    {
        var escaped = Escape(term);
        var (exact, withArticle, starts, wordStarts) = (escaped, $"the {escaped}", $"{escaped}%", $"% {escaped}%");
        return matches
            .OrderBy(s => EF.Functions.ILike(s.Title, exact, EscapeCharacter) || EF.Functions.ILike(s.Title, withArticle, EscapeCharacter) ? 0
                : EF.Functions.ILike(s.Title, starts, EscapeCharacter) || EF.Functions.ILike(s.Title, wordStarts, EscapeCharacter) ? 1
                : 2)
            .ThenByDescending(s => s.Popularity)
            .ThenBy(s => s.Title);
    }

    /// <summary>
    /// Shows among <paramref name="candidates"/> whose title is spelled nearly like the term, accents aside ("pokemon"
    /// finds "Pokémon"): titles that are the term but for accents first, then the most popular, since among near
    /// misses the well-known show is nearly always the one meant.
    /// </summary>
    public static async Task<List<Guid>> NearlySpelledAsync(IQueryable<Show> candidates, string term, int take, CancellationToken ct)
    {
        term = term.Trim();
        if (term.Length < MinFuzzyLength)
        {
            return [];
        }
        return await candidates
            .Select(s => new { s.Id, s.Popularity, Similarity = EF.Functions.TrigramsWordSimilarity(EF.Functions.Unaccent(term), EF.Functions.Unaccent(s.Title)) })
            .Where(x => x.Similarity >= MinSimilarity)
            .OrderByDescending(x => x.Similarity >= SameButForAccents)
            .ThenByDescending(x => x.Popularity)
            .Take(take)
            .Select(x => x.Id)
            .ToListAsync(ct);
    }
}
