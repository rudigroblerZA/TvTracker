using System.Globalization;
using System.Text.RegularExpressions;
using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Name">The first name as searched, in title case.</param>
/// <param name="Count">Characters with it as their first name.</param>
/// <param name="Rank">Its place among all first names (1 is the most common), or null when no character has it.</param>
/// <param name="Characters">The most popular shows' characters with that first name.</param>
/// <param name="Surnames">Characters with it as their last name.</param>
/// <param name="Decades">Its count and place in each decade it appears in, by the show's premiere, from decades with
/// <see cref="CharacterNamesApi.MinDecadeCharacters"/>+ characters.</param>
public record NameReportDto(string Name, int Count, int? Rank, IReadOnlyList<NamedCharacterDto> Characters, IReadOnlyList<NamedCharacterDto> Surnames,
    IReadOnlyList<NameDecadeDto> Decades);

/// <param name="ShowId">The catalog's show, when it's in the catalog.</param>
public record NamedCharacterDto(string Character, Guid PersonId, string Actor, Guid? ShowId, string Title, string? PosterUrl, int? Year);

/// <param name="Rank">Its place among the decade's first names.</param>
public record NameDecadeDto(int Decade, int Count, int Rank);

/// <param name="Women">The most common first names of characters played by women (by the actor's gender on TVmaze).</param>
/// <param name="Men">The same, played by men.</param>
/// <param name="Decades">Each decade's most common, from decades with <see cref="CharacterNamesApi.MinDecadeCharacters"/>+ characters.</param>
public record NameTopDto(int Characters, IReadOnlyList<NameCountDto> Women, IReadOnlyList<NameCountDto> Men, IReadOnlyList<NameDecadeTopDto> Decades);

public record NameCountDto(string Name, int Count);

public record NameDecadeTopDto(int Decade, int Characters, IReadOnlyList<NameCountDto> Women, IReadOnlyList<NameCountDto> Men);

/// <summary>
/// Characters' names: TV characters who share a first name, the most common names, and how a name rose and fell by the
/// decade a show premiered. Built from the provider's cast credits (main casts across TVmaze, not only the catalog's) and
/// the catalog's casts for actors whose credits aren't in yet, one row per actor and show.
/// <list type="bullet">
/// <item><b>Parsing</b> (<see cref="Parse"/>): the first of a character's aliases ("Frank Castle / The Punisher"), without
/// nicknames in quotes or parentheses; leading titles go ("Detective Chief Inspector", "Dr.", "Lady"), as do Jr. and
/// numerals at the end. What's left must be two or more capitalised words: a lone "Bartender" or "Eleven" can't be told
/// from a name. Self roles are out, by Universes' rule.</item>
/// <item><b>Decades</b> by the show's premiere, so a long-running show counts in the decade it began.</item>
/// <item><b>Women's and men's names</b> by the actor's gender on TVmaze, the only gender there is: a character's own can
/// differ (voice roles above all). Together the lists were men's names but for Sarah, Mary and Kate.</item>
/// </list>
/// The index is built once and cached <see cref="CacheFor"/>; a search is a filter over it in memory.
/// </summary>
public static partial class CharacterNamesApi
{
    public const int MinDecadeCharacters = 300;
    private const int TopOverall = 20;
    private const int TopPerDecade = 5;
    private const int MaxCharacters = 60;
    private const int MaxSurnames = 20;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);
    private const string Female = "Female";
    private const string Male = "Male";

    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "dr", "doctor", "mr", "mrs", "ms", "miss", "mister", "master", "madam", "madame", "detective", "det", "di", "ds", "dci", "dc",
        "dsi", "dsu", "pc", "wpc", "inspector", "constable", "superintendent", "supt", "chief", "captain", "capt", "agent", "special",
        "officer", "sgt", "sergeant", "lt", "lieutenant", "general", "colonel", "col", "major", "corporal", "cpl", "private", "pvt",
        "admiral", "commander", "cmdr", "ensign", "marshal", "ranger", "trooper", "sheriff", "deputy", "commissioner", "president",
        "vice", "senator", "governor", "mayor", "congressman", "congresswoman", "judge", "justice", "attorney", "district",
        "assistant", "ada", "da", "director", "professor", "prof", "coach", "nurse", "reverend", "rev", "pastor", "father", "fr",
        "sister", "brother", "mother", "uncle", "aunt", "auntie", "grandma", "grandpa", "granny", "sir", "dame", "lady", "lord",
        "king", "queen", "prince", "princess", "duke", "duchess", "earl", "count", "countess", "baron", "baroness", "emperor",
        "empress", "saint", "st", "the", "young", "old", "older", "little", "baby", "big", "dj", "ser"
    };

    /// <summary>Words that open a role, not a name ("Various Characters", "Unnamed Woman").</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "various", "unnamed", "unknown", "additional", "voice", "voices", "narrator", "host", "guest", "himself", "herself", "self",
        "man", "woman", "girl", "boy", "kid", "police", "security", "news", "party", "hospital", "prison", "store", "hotel"
    };

    private static readonly HashSet<string> Suffixes = new(StringComparer.OrdinalIgnoreCase) { "jr", "sr", "ii", "iii", "iv", "esq" };

    /// <summary>One character: their name's first and last word, the actor and the show.</summary>
    private sealed record Entry(string First, string Last, string Character, Guid PersonId, string Actor, string? Gender, Guid? ShowId,
        string Title, string? PosterUrl, int? Year, double Popularity);

    private sealed record Index(List<Entry> Entries, Dictionary<string, int> Ranks, Dictionary<int, Dictionary<string, int>> DecadeRanks,
        Dictionary<int, int> DecadeSizes);

    private sealed record CreditRow(Guid PersonId, string Actor, string? Gender, string? Character, string ShowExternalId, Guid? ShowId, string Title,
        string? PosterUrl, DateOnly? Premiered, double Popularity);

    public static void MapCharacterNamesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/character-names", GetTopAsync).RequireFeature(FeatureFlags.CharacterNames);
        api.MapGet("/character-names/{name}", GetNameAsync).RequireFeature(FeatureFlags.CharacterNames);
    }

    private static async Task<Ok<NameTopDto>> GetTopAsync(CatalogContext db, HybridCache cache, CancellationToken ct)
    {
        var index = await LoadAsync(db, cache, ct);
        var decades = index.Entries.Where(e => e.Year is not null).GroupBy(e => e.Year!.Value / 10 * 10)
            .Where(g => g.Count() >= MinDecadeCharacters)
            .OrderBy(g => g.Key)
            .Select(g => new NameDecadeTopDto(g.Key, g.Count(), Top(g.Where(e => e.Gender == Female), TopPerDecade),
                Top(g.Where(e => e.Gender == Male), TopPerDecade)))
            .ToList();
        return TypedResults.Ok(new NameTopDto(index.Entries.Count, Top(index.Entries.Where(e => e.Gender == Female), TopOverall),
            Top(index.Entries.Where(e => e.Gender == Male), TopOverall), decades));
    }

    private static async Task<Results<Ok<NameReportDto>, BadRequest<string>>> GetNameAsync(string name, CatalogContext db, HybridCache cache,
        CancellationToken ct)
    {
        var cleaned = name.Trim();
        if (cleaned.Length is < 2 or > 30 || !NameShape().IsMatch(cleaned))
        {
            return TypedResults.BadRequest("A first name of 2 to 30 letters.");
        }

        var index = await LoadAsync(db, cache, ct);
        var key = TitleCase(cleaned);
        var firsts = index.Entries.Where(e => e.First == key).ToList();
        // Only decades with enough characters for a place to mean something (the 1950s have a few dozen).
        var decades = firsts.Where(e => e.Year is not null).GroupBy(e => e.Year!.Value / 10 * 10)
            .Where(g => index.DecadeSizes[g.Key] >= MinDecadeCharacters).OrderBy(g => g.Key)
            .Select(g => new NameDecadeDto(g.Key, g.Count(), index.DecadeRanks[g.Key][key]))
            .ToList();
        return TypedResults.Ok(new NameReportDto(key, firsts.Count, index.Ranks.TryGetValue(key, out var rank) ? rank : null,
            Characters(firsts, MaxCharacters), Characters(index.Entries.Where(e => e.Last == key), MaxSurnames), decades));
    }

    private static List<NamedCharacterDto> Characters(IEnumerable<Entry> entries, int take) => entries
        .OrderBy(e => e.ShowId is null).ThenByDescending(e => e.Popularity).ThenBy(e => e.Title)
        .Take(take)
        .Select(e => new NamedCharacterDto(e.Character, e.PersonId, e.Actor, e.ShowId, e.Title, e.PosterUrl, e.Year))
        .ToList();

    private static List<NameCountDto> Top(IEnumerable<Entry> entries, int take) => entries.GroupBy(e => e.First)
        .Select(g => new NameCountDto(g.Key, g.Count()))
        .OrderByDescending(n => n.Count).ThenBy(n => n.Name, StringComparer.Ordinal)
        .Take(take)
        .ToList();

    /// <summary>Places by count, with ties sharing a place (two names with 40 characters are both 7th).</summary>
    private static Dictionary<string, int> Ranks(IEnumerable<Entry> entries)
    {
        var counts = entries.GroupBy(e => e.First).Select(g => (Name: g.Key, Count: g.Count())).OrderByDescending(n => n.Count).ToList();
        var ranks = new Dictionary<string, int>();
        for (var i = 0; i < counts.Count; i++)
        {
            ranks[counts[i].Name] = i > 0 && counts[i].Count == counts[i - 1].Count ? ranks[counts[i - 1].Name] : i + 1;
        }
        return ranks;
    }

    private static async Task<Index> LoadAsync(CatalogContext db, HybridCache cache, CancellationToken ct) =>
        await cache.GetOrCreateAsync("character-names", async token =>
        {
            var credits = await db.CastCredits.AsNoTracking()
                .Join(db.People, c => c.PersonId, p => p.Id, (c, p) => new CreditRow(c.PersonId, p.Name, p.Gender, c.Character, c.ShowExternalId,
                    c.ShowId, c.Title, c.PosterUrl, c.Premiered, c.Popularity))
                .ToListAsync(token);
            // Catalog casts fill in actors whose provider credits aren't fetched yet.
            var cast = await db.Cast.AsNoTracking()
                .Join(db.Shows, c => c.ShowId, s => s.Id, (c, s) => new CreditRow(c.PersonId, c.Person.Name, c.Person.Gender, c.Character, s.ExternalId,
                    s.Id, s.Title, s.PosterUrl, s.FirstAirDate, s.Popularity))
                .ToListAsync(token);
            var catalog = await db.Shows.AsNoTracking().Select(s => new { s.ExternalId, s.Id }).ToDictionaryAsync(s => s.ExternalId, s => s.Id, token);

            var entries = new List<Entry>();
            var seen = new HashSet<(Guid, string)>();
            foreach (var row in credits.Concat(cast))
            {
                if (row.Character is not { } character || !seen.Add((row.PersonId, row.ShowExternalId))
                    || Universes.IsSelfRole(character, row.Actor) || Parse(character) is not { } name)
                {
                    continue;
                }
                entries.Add(new Entry(name.First, name.Last, character, row.PersonId, row.Actor, row.Gender,
                    row.ShowId ?? (catalog.TryGetValue(row.ShowExternalId, out var id) ? id : null), row.Title, row.PosterUrl,
                    row.Premiered?.Year, row.Popularity));
            }

            var decades = entries.Where(e => e.Year is not null).GroupBy(e => e.Year!.Value / 10 * 10).ToList();
            return new Index(entries, Ranks(entries), decades.ToDictionary(g => g.Key, g => Ranks(g)), decades.ToDictionary(g => g.Key, g => g.Count()));
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);

    /// <summary>A character's first and last name, title-cased, or null when it doesn't read as a person's name.</summary>
    internal static (string First, string Last)? Parse(string character)
    {
        var alias = Quoted().Replace(character.Split('/')[0], " ");
        var words = alias.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('.', ':', ';')).Where(w => w.Length > 0).ToList();
        while (words.Count > 0 && Titles.Contains(words[0]))
        {
            words.RemoveAt(0);
        }
        while (words.Count > 0 && Suffixes.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }
        if (words.Count < 2 || NotNames.Contains(words[0]) || !words.All(w => Word().IsMatch(w)))
        {
            return null;
        }
        return (TitleCase(words[0]), TitleCase(words[^1]));
    }

    private static string TitleCase(string word) =>
        string.Concat(word[..1].ToUpper(CultureInfo.InvariantCulture), word[1..].ToLower(CultureInfo.InvariantCulture));

    [GeneratedRegex("\"[^\"]*\"|“[^”]*”|\\([^)]*\\)")]
    private static partial Regex Quoted();

    /// <summary>A capitalised word: a letter then letters, apostrophes or hyphens ("O'Brien", "Mary-Kate", "McNulty").</summary>
    [GeneratedRegex(@"^\p{Lu}[\p{L}'’\-]+$")]
    private static partial Regex Word();

    [GeneratedRegex(@"^\p{L}[\p{L}'’\-]*$")]
    private static partial Regex NameShape();
}
