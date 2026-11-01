using System.Text.RegularExpressions;
using Catalog.API.Services;
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

public record FormulaTitleDto(Guid EpisodeId, int Season, int Number, string Title);

/// <param name="Phrases">Shows whose titles open with the same phrase.</param>
/// <param name="Numbered">Shows numbering their episodes as chapters, parts or days.</param>
/// <param name="Articles">Shows where nine in ten titles or more start with the same article ("The", or "Die", "Le", … in
/// other languages).</param>
public record TitleFormulaListsDto(IReadOnlyList<FormulaShowDto> Phrases, IReadOnlyList<FormulaShowDto> Numbered, IReadOnlyList<FormulaShowDto> Articles);

public record FormulaShowDto(Guid ShowId, string Title, string? PosterUrl, string Prefix, int Matching, int Titled, string? Example);

/// <summary>
/// Title formulas: shows whose episode titles follow a pattern, found in the catalog's titles rather than listed by hand.
/// <list type="bullet">
/// <item><b>The opening words:</b> for each show, the most common opening of one, two and three words (case and trailing
/// punctuation ignored). A phrase of two or three words counts from <see cref="MinPhraseShare"/> of the titles, one word
/// from <see cref="MinWordShare"/>, and an article ("The", "A", or "Die", "Le", … in other languages) only from
/// <see cref="MinArticleShare"/>, since a third of all titles start with one. Of those, the best is the share weighted a quarter more per extra word: Friends' "The One" (234 of
/// 236) beats both "The" (236) and "The One Where" (about half).</item>
/// <item><b>Numbered:</b> an opening of "Chapter", "Part", "Day" and the like (in a few languages) followed by a number,
/// in digits, words or Roman numerals, for most of its titles (Stranger Things' "Chapter One: The Vanishing of Will
/// Byers", 24's "Day 1: 12:00 a.m.-1:00 a.m.").</item>
/// <item><b>Placeholders out:</b> "Episode 5", "Chapter 3" with nothing after it, "TBA" and "Untitled" are TVmaze's
/// stand-ins for a missing title, not a formula. A show needs <see cref="MinTitled"/> real titles.</item>
/// <item><b>Exceptions</b> are often the notable episodes (Friends' finale is "The Last One"). Every title carries its
/// episode id, so the page keeps the ones the viewer hasn't watched behind the spoiler shield.</item>
/// <item><b>Daily shows out</b> (a season over <see cref="ShowRules.DailySeason"/> episodes), as Episode lengths and Between seasons
/// leave them out: their titles are guests' names and dates.</item>
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

    private sealed record TitleRow(Guid Id, Guid ShowId, int SeasonNumber, int EpisodeNumber, string Title);

    /// <summary>A title split into words: <paramref name="Keys"/> for matching, <paramref name="Raw"/> as written, aligned.</summary>
    private sealed record Tokens(string[] Keys, string[] Raw);

    private sealed record ShowRow(Guid Id, string Title, string? PosterUrl, double Popularity, bool Story);

    /// <summary>Every formula by show id, so a show page's lookup isn't a scan.</summary>
    private sealed record AllFormulas(Dictionary<Guid, TitleFormulaDto> Shows, List<ShowRow> Titles);

    public static void MapTitleFormulasApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/title-formula", GetShowAsync).RequireFeature(FeatureFlags.TitleFormulas);
        api.MapGet("/title-formulas", GetListsAsync).RequireFeature(FeatureFlags.TitleFormulas);
    }

    private static async Task<Results<Ok<TitleFormulaDto>, NotFound>> GetShowAsync(Guid id, CatalogContext db, HybridCache cache, CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, ct);
        return all.Shows.TryGetValue(id, out var formula) ? TypedResults.Ok(formula) : TypedResults.NotFound();
    }

    private static async Task<Ok<TitleFormulaListsDto>> GetListsAsync(CatalogContext db, HybridCache cache, CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, ct);
        // Story shows only (scripted and animated): talk and reality shows' titles are guests' names and dates.
        var titles = all.Titles.Where(t => t.Story).ToDictionary(t => t.Id);
        List<FormulaShowDto> Of(string kind) => all.Shows.Values
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
                .Select(e => new TitleRow(e.Id, e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Title))
                .ToListAsync(token);
            var formulas = rows.GroupBy(r => r.ShowId)
                .Where(g => g.GroupBy(r => r.SeasonNumber).All(season => season.Count() <= ShowRules.DailySeason))
                .Select(g => Find(g.Key, g.Where(r => !string.IsNullOrWhiteSpace(r.Title) && !Placeholder().IsMatch(r.Title.Trim()))
                    .OrderBy(r => r.SeasonNumber).ThenBy(r => r.EpisodeNumber).ToList()))
                .OfType<TitleFormulaDto>()
                .ToDictionary(f => f.ShowId);
            var ids = formulas.Keys.ToList();
            var titles = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new ShowRow(s.Id, s.Title, s.PosterUrl, s.Popularity, s.Keywords.Any(k => ShowRules.StoryKinds.Contains(k))))
                .ToListAsync(token);
            return new AllFormulas(formulas, titles);
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);

    /// <summary>A show's formula, or null when its titles don't follow one.</summary>
    private static TitleFormulaDto? Find(Guid showId, List<TitleRow> rows)
    {
        // Titles that are only punctuation have no words: they're no more a title than a blank one.
        var episodes = rows.Select(e => (Row: e, Tokens: Split(e.Title))).Where(e => e.Tokens.Keys.Length > 0).ToList();
        if (episodes.Count < MinTitled)
        {
            return null;
        }

        (string[] Key, int Count, double Score)? best = null;
        for (var k = 1; k <= 3; k++)
        {
            // Only titles longer than the opening: a one-word "Pilot" opens nothing.
            var top = episodes.Select(e => e.Tokens.Keys).Where(w => w.Length > k)
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

        var length = chosen.Key.Length;
        bool Follows(Tokens t) => t.Keys.Length > length && t.Keys.Take(length).SequenceEqual(chosen.Key);
        var following = episodes.Where(e => Follows(e.Tokens)).ToList();
        var kind = length == 1 && Articles.Contains(chosen.Key[0]) ? "article"
            : NumberedWords.Contains(chosen.Key[0]) && following.Count(e => IsNumber(e.Tokens.Keys[1])) >= 0.8 * following.Count ? "numbered"
            : "phrase";
        // The prefix as titles write it: the most common spelling of the very words that matched.
        var prefix = following.Select(e => string.Join(' ', e.Tokens.Raw.Take(length))).GroupBy(p => p).MaxBy(g => g.Count())!.Key;
        return new TitleFormulaDto(showId, kind, prefix, chosen.Count, episodes.Count,
            following.Take(3).Select(e => Title(e.Row)).ToList(),
            episodes.Where(e => !Follows(e.Tokens)).Take(MaxExceptions).Select(e => Title(e.Row)).ToList());
    }

    private static FormulaTitleDto Title(TitleRow e) => new(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title.Trim());

    /// <summary>
    /// A title's words: each key lower case, without trailing punctuation, leading quotes or a possessive ("Chapter One:"
    /// is "chapter", "one"; "Mr. Monk's" follows "Mr. Monk"), and beside it the word as written, with only its
    /// trailing separators and leading quotes trimmed. A word that is only punctuation (a lone "–") is dropped from both.
    /// </summary>
    private static Tokens Split(string title)
    {
        var keys = new List<string>();
        var raw = new List<string>();
        foreach (var word in title.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var written = word.TrimEnd(':', ',', ';', '-', '–', '—').TrimStart('"', '“', '\'');
            var key = written.ToLowerInvariant().TrimEnd('.', '!', '?');
            key = key.EndsWith("'s") || key.EndsWith("’s") ? key[..^2] : key;
            if (key.Length > 0)
            {
                keys.Add(key);
                raw.Add(written);
            }
        }
        return new Tokens([.. keys], [.. raw]);
    }

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
