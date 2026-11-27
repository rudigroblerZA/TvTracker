using System.Text.Json;

namespace Catalog.ImportCli;

/// <summary>
/// One search to check: a description, and the show it should find; or, with no <paramref name="Expect"/>, a search
/// about something no show is about, which should find nothing by meaning.
/// </summary>
/// <param name="Expect">The show's title, as the catalog has it.</param>
/// <param name="Year">The year it started, to tell it from another show with the same title (The Office, Doctor Who).</param>
public sealed record SearchCase(string Query, string? Expect = null, int? Year = null);

/// <param name="Rank">Where the expected show came in the results as a member sees them, from 1; null when it wasn't there.</param>
/// <param name="ByMeaning">How many results matched by meaning and not by title.</param>
/// <param name="First">The first results' titles.</param>
/// <param name="Skipped">Why it wasn't run (the expected show isn't in the catalog, or several shows could be it).</param>
public sealed record SearchResult(string Query, string? Expect, int? Rank, int ByMeaning, string[] First, string? Skipped = null);

/// <param name="Searches">Searches with an expected show that were run.</param>
/// <param name="Top1">How many put it first.</param>
/// <param name="Top3">How many put it in the first three.</param>
/// <param name="Found">How many found it at all.</param>
/// <param name="Mrr">Mean reciprocal rank: 1 when every one is first, 0 when none is found.</param>
/// <param name="Unrelated">Searches about nothing a show is about.</param>
/// <param name="UnrelatedQuiet">How many of those found nothing by meaning.</param>
public sealed record SearchScore(int Searches, int Top1, int Top3, int Found, double Mrr, int Unrelated, int UnrelatedQuiet);

/// <summary>
/// Measures search by meaning against a fixed list of searches (docs/design/search-queries.json): for each
/// description, where the show it describes comes in the results; for each unrelated search, whether it found
/// anything. The same list run before and after a change to the model, its cutoff or the text that is embedded says
/// whether the change helped. Read-only: it searches the catalog as it is and asks the provider for nothing.
/// </summary>
public static class SearchEval
{
    /// <summary>
    /// Results asked for per search: every title match a description is likely to have, the near-spellings and all
    /// the matches by meaning.
    /// </summary>
    private const int PageSize = 100;

    private static readonly JsonSerializerOptions Json = EvalHttp.Json;

    /// <summary>
    /// The exit code when a floor was asked for and no search with an expected show could be run: nothing was measured,
    /// which is neither a pass nor a worse search.
    /// </summary>
    public const int NothingMeasured = 2;

    /// <param name="failUnder">Exit code 1 when the mean reciprocal rank is under this; <see cref="NothingMeasured"/> when there was nothing to measure.</param>
    public static async Task<int> RunAsync(string catalogUrl, string casesFile, string? outFile, string? baselineFile, double? failUnder, CancellationToken ct)
    {
        var cases = JsonSerializer.Deserialize<SearchCase[]>(await File.ReadAllTextAsync(casesFile, ct), Json) ?? [];
        var baseline = baselineFile is null
            ? []
            : (JsonSerializer.Deserialize<SearchResult[]>(await File.ReadAllTextAsync(baselineFile, ct), Json) ?? []).ToDictionary(r => r.Query);

        using var http = EvalHttp.Client(catalogUrl);
        var results = new List<SearchResult>();
        foreach (var search in cases)
        {
            var result = await RunAsync(http, search, ct);
            results.Add(result);
            Console.WriteLine(Describe(result, baseline.GetValueOrDefault(result.Query)));
        }

        if (outFile is not null)
        {
            await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(results, Json), ct);
        }

        var score = Score(results);
        Console.WriteLine();
        Console.WriteLine(Describe(score));
        if (results.Count(r => r.Skipped is not null) is > 0 and var skipped)
        {
            Console.WriteLine($"{skipped} skipped: the expected show isn't in this catalog, or several shows could be it.");
        }
        if (baseline.Count > 0)
        {
            // Like for like: only the searches that ran both times.
            var both = results.Where(r => r.Skipped is null && baseline.GetValueOrDefault(r.Query) is { Skipped: null }).ToList();
            Console.WriteLine($"Of the {both.Count} searches run both times. Now: {Describe(Score(both))}");
            Console.WriteLine($"Baseline: {Describe(Score(both.Select(r => baseline[r.Query]).ToList()))}");
        }
        if (failUnder is not { } floor)
        {
            return 0;
        }
        if (score.Searches == 0)
        {
            Console.WriteLine(results.Any(r => r.Skipped is not null)
                ? "Nothing to measure: every search with an expected show was skipped, for the reasons above."
                : "Nothing to measure: no search in the list names an expected show.");
            return NothingMeasured;
        }
        return score.Mrr < floor ? 1 : 0;
    }

    public static SearchScore Score(IReadOnlyCollection<SearchResult> results)
    {
        var ran = results.Where(r => r.Skipped is null).ToList();
        var expecting = ran.Where(r => r.Expect is not null).ToList();
        var unrelated = ran.Where(r => r.Expect is null).ToList();
        return new SearchScore(
            expecting.Count,
            expecting.Count(r => r.Rank == 1),
            expecting.Count(r => r.Rank <= 3),
            expecting.Count(r => r.Rank is not null),
            expecting.Count == 0 ? 0 : expecting.Average(r => r.Rank is { } rank ? 1.0 / rank : 0),
            unrelated.Count,
            unrelated.Count(r => r.ByMeaning == 0));
    }

    /// <summary>
    /// Where the show <paramref name="expect"/> comes in <paramref name="results"/>, from 1; null when it isn't there.
    /// By id: another show with the same title is not it.
    /// </summary>
    public static int? RankOf(List<Guid> results, Guid expect) => results.IndexOf(expect) is >= 0 and var index ? index + 1 : null;

    /// <summary>
    /// The one show among <paramref name="suggestions"/> that a case means: titled so, and from that year when the case
    /// gives one. Null when there is none, or more than one (the case then needs a year).
    /// </summary>
    public static Guid? Expected(IEnumerable<(Guid Id, string Title, int? Year)> suggestions, string title, int? year)
    {
        var matches = suggestions
            .Where(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase) && (year is null || s.Year == year))
            .Select(s => s.Id)
            .Distinct()
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Why a case's expected show couldn't be told: what the catalog has under that title against what the case asked for.
    /// </summary>
    /// <param name="years">The start year of each show in the catalog with that title.</param>
    public static string WhySkipped(string title, int? year, IReadOnlyList<int?> years)
    {
        var found = string.Join(", ", years.Select(y => y?.ToString() ?? "no year"));
        return (years.Count, year) switch
        {
            (0, _) => $"{title} isn't in the catalog",
            (_, { } wanted) when !years.Contains(wanted) => $"no show titled {title} started in {wanted} (the catalog has: {found})",
            (_, { } wanted) => $"{years.Count(y => y == wanted)} shows titled {title} started in {wanted}",
            _ => $"{years.Count} shows are titled {title} ({found}): give the case a year"
        };
    }

    private static async Task<SearchResult> RunAsync(HttpClient http, SearchCase search, CancellationToken ct)
    {
        Guid? expected = null;
        if (search.Expect is { } expect)
        {
            var suggestions = await EvalHttp.GetAsync<List<Show>>(http, $"api/v1/shows/suggest?q={Uri.EscapeDataString(expect)}", ct)
                ?? throw new CliException($"No catalog at {http.BaseAddress}: is the address right?");
            expected = Expected(suggestions.Select(s => (s.Id, s.Title, s.Year)), expect, search.Year);
            if (expected is null)
            {
                var titled = suggestions.Where(s => string.Equals(s.Title, expect, StringComparison.OrdinalIgnoreCase)).ToList();
                return new SearchResult(search.Query, expect, null, 0, [], WhySkipped(expect, search.Year, titled.Select(s => s.Year).ToList()));
            }
        }

        var page = await EvalHttp.GetAsync<Page>(http, $"api/v1/shows?search={Uri.EscapeDataString(search.Query)}&pageSize={PageSize}&import=false", ct)
            ?? throw new CliException($"No catalog at {http.BaseAddress}: is the address right?");
        var titles = page.Items.Select(s => s.Title).ToList();
        return new SearchResult(search.Query, search.Expect, expected is { } id ? RankOf(page.Items.Select(s => s.Id).ToList(), id) : null,
            page.TotalCount - (page.TitleCount ?? 0), titles.Take(3).ToArray());
    }

    private static string Describe(SearchScore score) =>
        $"First for {score.Top1} of {score.Searches}, in the first three for {score.Top3}, found for {score.Found}; MRR {score.Mrr:0.00}. " +
        $"{score.UnrelatedQuiet} of {score.Unrelated} unrelated searches found nothing by meaning.";

    private static string Describe(SearchResult result, SearchResult? before)
    {
        if (result.Skipped is { } why)
        {
            return $"SKIP  {result.Query}: {why}";
        }

        var was = before is { Skipped: null } && (before.Rank != result.Rank || (result.Expect is null && before.ByMeaning != result.ByMeaning))
            ? result.Expect is null ? $" (was {before.ByMeaning})" : $" (was {(before.Rank is { } r ? $"#{r}" : "not found")})"
            : "";
        if (result.Expect is null)
        {
            return result.ByMeaning == 0
                ? $"QUIET {result.Query}{was}"
                : $"NOISE {result.Query}: {result.ByMeaning} by meaning{was}, first {string.Join(", ", result.First)}";
        }
        return result.Rank is { } rank
            ? $"#{rank,-4} {result.Query} → {result.Expect}{was}"
            : $"MISS  {result.Query} → {result.Expect}{was}; first {string.Join(", ", result.First)}";
    }

    private sealed record Show(Guid Id, string Title, int? Year);

    private sealed record Page(List<Show> Items, int TotalCount, int? TitleCount);
}
