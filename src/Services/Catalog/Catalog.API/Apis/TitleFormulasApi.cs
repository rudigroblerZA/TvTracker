using System.Text.RegularExpressions;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Kind">"phrase" (Friends' "The One"), "numbered" ("Chapter One: …", "Part 3") or "article" (every title
/// starts "The").</param>
/// <param name="Prefix">The opening words, as most titles write them.</param>
/// <param name="Matching">Titled regular episodes that open with it.</param>
/// <param name="Titled">Titled regular episodes, placeholders such as "Episode 5" left out.</param>
/// <param name="Examples">The first three that follow it, in air order.</param>
/// <param name="Exceptions">The ones that don't, in air order (at most <see cref="TitleFormulasApi.MaxExceptions"/>).</param>
public record TitleFormulaDto(Guid ShowId, string Kind, string Prefix, int Matching, int Titled, IReadOnlyList<FormulaTitleDto> Examples,
    IReadOnlyList<FormulaTitleDto> Exceptions);

public record FormulaTitleDto(int Season, int Number, string Title);

/// <param name="Phrases">Shows whose titles open with the same phrase.</param>
/// <param name="Numbered">Shows numbering their episodes as chapters, parts or days.</param>
/// <param name="Articles">Shows where every title (or nearly) starts "The".</param>
public record TitleFormulaListsDto(IReadOnlyList<FormulaShowDto> Phrases, IReadOnlyList<FormulaShowDto> Numbered, IReadOnlyList<FormulaShowDto> Articles);

public record FormulaShowDto(Guid ShowId, string Title, string? PosterUrl, string Prefix, int Matching, int Titled, string? Example);

/// <summary>
/// Title formulas: shows whose episode titles follow a pattern, found in the catalog's titles rather than listed by hand.
/// <list type="bullet">
/// <item><b>The opening words:</b> for each show, the most common opening of one, two and three words (case and trailing
/// punctuation ignored). A phrase of two or three words counts from <see cref="MinPhraseShare"/> of the titles, one word
/// from <see cref="MinWordShare"/>, and "The" or "A" only from <see cref="MinArticleShare"/>, since a third of all titles
/// start with one. Of those, the best is the share weighted a quarter more per extra word: Friends' "The One" (234 of
/// 236) beats both "The" (236) and "The One Where" (about half).</item>
/// <item><b>Numbered:</b> an opening of "Chapter", "Part", "Day" and the like (in a few languages) followed by a number,
/// in digits, words or Roman numerals, for most of its titles (Stranger Things' "Chapter One: The Vanishing of Will
/// Byers", 24's "Day 1: 12:00 a.m.-1:00 a.m.").</item>
/// <item><b>Placeholders out:</b> "Episode 5", "Chapter 3" with nothing after it, "TBA" and "Untitled" are TVmaze's
/// stand-ins for a missing title, not a formula. A show needs <see cref="MinTitled"/> real titles.</item>
/// <item><b>Exceptions</b> are often the notable episodes (Friends' finale is "The Last One"), so the page keeps them
/// behind the spoiler shield.</item>
/// </list>
/// Every show's formula is worked out together from one query of regular episodes' titles, cached <see cref="CacheFor"/>.
/// </summary>
public static partial class TitleFormulasApi
{
    public const double MinPhraseShare = 0.4;
    public const double MinWordShare = 0.6;
    public const double MinArticleShare = 0.9;
    public const int MinTitled = 6;
    public const int MaxExceptions = 6;
    private const int MinMatching = 5;
    private const int ListSize = 30;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    /// <summary>Articles in the languages the catalog has most: an opening article alone is no phrase.</summary>
    private static readonly HashSet<string> Articles = ["the", "a", "an", "die", "der", "das", "le", "la", "les", "el", "los", "las", "il", "het", "de"];

    private static readonly HashSet<string> NumberedWords =
        ["chapter", "part", "day", "act", "book", "hour", "episode", "chapitre", "capítulo", "capitulo", "kapitel", "capitolo", "teil", "deel", "folge", "night", "odcinek", "episodio", "épisode"];

    private static readonly HashSet<string> NumberWords =
    [
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen",
        "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty", "first", "second", "third", "fourth", "fifth", "sixth",
        "seventh", "eighth", "ninth", "tenth", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety", "hundred", "final", "last", "un", "deux", "trois", "uno", "dos", "tres", "eins", "zwei", "drei"
    ];

    private sealed record TitleRow(Guid ShowId, int SeasonNumber, int EpisodeNumber, string Title);

    private sealed record ShowRow(Guid Id, string Title, string? PosterUrl, double Popularity, bool Story);

    private sealed record AllFormulas(List<TitleFormulaDto> Shows, List<ShowRow> Titles);

    public static void MapTitleFormulasApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/title-formula", GetShowAsync).RequireFeature(FeatureFlags.TitleFormulas);
        api.MapGet("/title-formulas", GetListsAsync).RequireFeature(FeatureFlags.TitleFormulas);
    }

    private static async Task<Results<Ok<TitleFormulaDto>, NotFound>> GetShowAsync(Guid id, CatalogContext db, HybridCache cache, CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, ct);
        return all.Shows.FirstOrDefault(s => s.ShowId == id) is { } formula ? TypedResults.Ok(formula) : TypedResults.NotFound();
    }

    private static async Task<Ok<TitleFormulaListsDto>> GetListsAsync(CatalogContext db, HybridCache cache, CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, ct);
        // Story shows only (scripted and animated): talk and reality shows' titles are guests' names and dates.
        var titles = all.Titles.Where(t => t.Story).ToDictionary(t => t.Id);
        List<FormulaShowDto> Of(string kind) => all.Shows
            .Where(f => f.Kind == kind && titles.ContainsKey(f.ShowId))
            .OrderByDescending(f => titles[f.ShowId].Popularity).ThenByDescending(f => f.Matching)
            .Take(ListSize)
            .Select(f => new FormulaShowDto(f.ShowId, titles[f.ShowId].Title, titles[f.ShowId].PosterUrl, f.Prefix, f.Matching, f.Titled,
                f.Examples.FirstOrDefault()?.Title))
            .ToList();
        return TypedResults.Ok(new TitleFormulaListsDto(Of("phrase"), Of("numbered"), Of("article")));
    }

    private static async Task<AllFormulas> LoadAsync(CatalogContext db, HybridCache cache, CancellationToken ct) =>
        await cache.GetOrCreateAsync("title-formulas", async token =>
        {
            var rows = await db.Episodes.AsNoTracking()
                .Where(e => e.SeasonNumber > 0 && e.Title != "")
                .Select(e => new TitleRow(e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Title))
                .ToListAsync(token);
            var formulas = rows.Where(r => !Placeholder().IsMatch(r.Title.Trim()))
                .GroupBy(r => r.ShowId)
                .Select(g => Find(g.Key, g.OrderBy(r => r.SeasonNumber).ThenBy(r => r.EpisodeNumber).ToList()))
                .OfType<TitleFormulaDto>()
                .ToList();
            var ids = formulas.Select(f => f.ShowId).ToList();
            var titles = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new ShowRow(s.Id, s.Title, s.PosterUrl, s.Popularity, s.Keywords.Contains("scripted") || s.Keywords.Contains("animation")))
                .ToListAsync(token);
            return new AllFormulas(formulas, titles);
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);

    /// <summary>A show's formula, or null when its titles don't follow one.</summary>
    private static TitleFormulaDto? Find(Guid showId, List<TitleRow> episodes)
    {
        if (episodes.Count < MinTitled)
        {
            return null;
        }

        var words = episodes.Select(e => Words(e.Title)).ToList();
        (string[] Key, int Count, double Score)? best = null;
        for (var k = 1; k <= 3; k++)
        {
            // Only titles longer than the opening: a one-word "Pilot" opens nothing.
            var top = words.Where(w => w.Length > k)
                .GroupBy(w => string.Join(' ', w.Take(k)))
                .Select(g => (Key: g.Key.Split(' '), Count: g.Count()))
                .OrderByDescending(g => g.Count)
                .FirstOrDefault(); // a value tuple: MaxBy throws on an empty sequence
            if (top.Key is null || top.Count < MinMatching)
            {
                continue;
            }
            var share = (double)top.Count / episodes.Count;
            var article = k == 1 && Articles.Contains(top.Key[0]);
            var needed = article ? MinArticleShare : k == 1 ? MinWordShare : MinPhraseShare;
            if (share < needed)
            {
                continue;
            }
            var score = share * (1 + 0.25 * (k - 1)) * (article ? 0.5 : 1);
            if (best is null || score > best.Value.Score)
            {
                best = (top.Key, top.Count, score);
            }
        }
        if (best is not { } chosen)
        {
            return null;
        }

        bool Follows(string[] w) => w.Length > chosen.Key.Length && w.Take(chosen.Key.Length).SequenceEqual(chosen.Key);
        var following = episodes.Where((_, i) => Follows(words[i])).ToList();
        var kind = chosen.Key.Length == 1 && Articles.Contains(chosen.Key[0]) ? "article"
            : NumberedWords.Contains(chosen.Key[0])
              && following.Count(e => Words(e.Title) is { Length: > 1 } w && IsNumber(w[1])) >= 0.8 * following.Count ? "numbered"
            : "phrase";
        // The prefix as titles write it: the most common original spelling of those opening words.
        var prefix = following.Select(e => string.Join(' ', e.Title.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(chosen.Key.Length)).TrimEnd(':', ',', '-', ' ', '–', '—'))
            .GroupBy(p => p).MaxBy(g => g.Count())!.Key;
        return new TitleFormulaDto(showId, kind, prefix, chosen.Count, episodes.Count,
            following.Take(3).Select(Title).ToList(),
            episodes.Where((_, i) => !Follows(words[i])).Take(MaxExceptions).Select(Title).ToList());
    }

    private static FormulaTitleDto Title(TitleRow e) => new(e.SeasonNumber, e.EpisodeNumber, e.Title.Trim());

    /// <summary>A title's words, lower case, without trailing punctuation or a possessive ("Chapter One:" is "chapter", "one").</summary>
    private static string[] Words(string title) => title.Trim().ToLowerInvariant()
        .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.TrimEnd(':', ',', ';', '-', '–', '—', '.', '!', '?').TrimStart('"', '“', '\''))
        .Select(w => w.EndsWith("'s") || w.EndsWith("’s") ? w[..^2] : w) // "Mr. Monk's 100th Case" follows "Mr. Monk"
        .Where(w => w.Length > 0)
        .ToArray();

    /// <summary>Digits, Roman numerals or number words, hyphenated ones too ("twenty-one").</summary>
    private static bool IsNumber(string word) =>
        Digits().IsMatch(word) || Roman().IsMatch(word) || word.Split('-').All(NumberWords.Contains);

    /// <summary>TVmaze's stand-ins for a missing title.</summary>
    [GeneratedRegex(@"^((episode|chapter|part|folge|capítulo|episodio|épisode|odcinek|aflevering|jakso|avsnitt)\s*#?\s*[\d.]+|tba|tbd|untitled.*|episode\s+#?[\d]+\.[\d]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^\d+[\d:.]*$")]
    private static partial Regex Digits();

    [GeneratedRegex("^(i|ii|iii|iv|v|vi|vii|viii|ix|x|xi|xii|xiii|xiv|xv|xvi|xvii|xviii|xix|xx)$")]
    private static partial Regex Roman();
}
