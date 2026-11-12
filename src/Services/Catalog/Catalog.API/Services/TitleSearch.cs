using System.Linq.Expressions;
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
    /// Near-spelled names are looked for only when fewer names than this contain the term: with plenty of real
    /// matches, look-alikes are noise.
    /// </summary>
    public const int FuzzyBelow = 5;

    /// <summary>
    /// Shorter terms have too few trigrams to tell a near miss from noise.
    /// </summary>
    private const int MinFuzzyLength = 4;

    /// <summary>
    /// Whether the term is long enough to look for names spelled nearly like it.
    /// </summary>
    public static bool AllowsNearSpelling(string term) => term.Trim().Length >= MinFuzzyLength;

    /// <summary>
    /// The term with LIKE's wildcards taken literally (backslash is Postgres's default LIKE escape).
    /// </summary>
    public static string Escape(string term) =>
        term.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>
    /// Shows whose title contains the term.
    /// </summary>
    public static IQueryable<Show> Matching(IQueryable<Show> shows, string term) => shows.Where(Contains<Show>(s => s.Title, term));

    public static IQueryable<Show> NotMatching(IQueryable<Show> shows, string term)
    {
        var contains = Contains<Show>(s => s.Title, term);
        return shows.Where(Expression.Lambda<Func<Show, bool>>(Expression.Not(contains.Body), contains.Parameters));
    }

    /// <summary>
    /// People whose name contains the term.
    /// </summary>
    public static IQueryable<Person> Matching(IQueryable<Person> people, string term) => people.Where(Contains<Person>(p => p.Name, term));

    /// <summary>
    /// Best first: the title that is the term (with or without a leading "The"), then titles with a word starting
    /// with it, then the rest; most popular first within each.
    /// </summary>
    public static IQueryable<Show> Ranked(IQueryable<Show> matches, string term)
    {
        var escaped = Escape(term);
        return matches
            .Select(s => new { Show = s, Name = CatalogContext.SearchText(s.Title), Term = CatalogContext.SearchText(escaped) })
            .OrderBy(x => EF.Functions.Like(x.Name, x.Term) || EF.Functions.Like(x.Name, "the " + x.Term) ? 0
                : EF.Functions.Like(x.Name, x.Term + "%") || EF.Functions.Like(x.Name, "% " + x.Term + "%") ? 1
                : 2)
            .ThenByDescending(x => x.Show.Popularity)
            .ThenBy(x => x.Show.Title)
            .ThenBy(x => x.Show.Id)
            .Select(x => x.Show);
    }

    /// <summary>
    /// Shows among <paramref name="candidates"/> whose title is spelled nearly like the term (ask
    /// <see cref="AllowsNearSpelling"/> first). Closest first in broad bands (a fifth of the scale), and within a band
    /// the most popular: among near misses about as close, the well-known show is nearly always the one meant
    /// ("strangr things" is Stranger Things, not 109 Strange Things).
    /// </summary>
    public static IQueryable<Show> NearlySpelled(IQueryable<Show> candidates, string term)
    {
        term = term.Trim();
        return candidates.Where(Near<Show>(s => s.Title, term))
            .OrderByDescending(s => Math.Floor(EF.Functions.TrigramsWordSimilarity(CatalogContext.SearchText(term), CatalogContext.SearchText(s.Title)) * 5))
            .ThenByDescending(s => s.Popularity)
            .ThenBy(s => s.Title)
            .ThenBy(s => s.Id);
    }

    /// <summary>
    /// People whose name is spelled nearly like the term (ask <see cref="AllowsNearSpelling"/> first).
    /// </summary>
    public static IQueryable<Person> NearlySpelled(IQueryable<Person> people, string term) => people.Where(Near<Person>(p => p.Name, term.Trim()));

    private static Expression<Func<T, bool>> Contains<T>(Expression<Func<T, string>> name, string term)
    {
        var escaped = Escape(term);
        return On(name, n => EF.Functions.Like(CatalogContext.SearchText(n), "%" + CatalogContext.SearchText(escaped) + "%"));
    }

    /// <summary>
    /// The indexed <c>&lt;%</c> finds the candidates, at the threshold each catalog connection carries (see
    /// CatalogDataExtensions); the score is then checked here too, so the cutoff is in the query whatever the
    /// connection's setting.
    /// </summary>
    private static Expression<Func<T, bool>> Near<T>(Expression<Func<T, string>> name, string term) =>
        On(name, n => EF.Functions.TrigramsAreWordSimilar(CatalogContext.SearchText(term), CatalogContext.SearchText(n))
            && EF.Functions.TrigramsWordSimilarity(CatalogContext.SearchText(term), CatalogContext.SearchText(n)) >= CatalogContext.NearSpellingThreshold);

    /// <summary>
    /// A test on a name, as a test on whatever has the name: <paramref name="test"/> with its parameter replaced by
    /// <paramref name="name"/>'s body, so shows' titles and people's names share one predicate EF can still translate.
    /// </summary>
    private static Expression<Func<T, bool>> On<T>(Expression<Func<T, string>> name, Expression<Func<string, bool>> test) =>
        Expression.Lambda<Func<T, bool>>(new Substitute(test.Parameters[0], name.Body).Visit(test.Body), name.Parameters);

    private sealed class Substitute(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
