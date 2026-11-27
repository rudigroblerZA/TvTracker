using System.Text.Json;

namespace Catalog.ImportCli;

/// <summary>
/// One search to check: a description, and the show it should find; or, with no <paramref name="Expect"/>, a search
/// about something no show is about, which should find nothing by meaning.
/// </summary>
/// <param name="Expect">The show's title, as the catalog has it.</param>
public sealed record SearchCase(string Query, string? Expect = null);

/// <param name="Rank">Where the expected show came in the results as a member sees them, from 1; null when it wasn't there.</param>
/// <param name="ByMeaning">How many results matched by meaning and not by title.</param>
/// <param name="First">The first results' titles.</param>
/// <param name="Skipped">Why it wasn't run (the expected show isn't in the catalog).</param>
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

    /// <param name="failUnder">Exit code 1 when the mean reciprocal rank is under this.</param>
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
            Console.WriteLine($"{skipped} skipped: the expected show isn't in this catalog.");
        }
        if (baseline.Count > 0)
        {
            Console.WriteLine($"Baseline: {Describe(Score(baseline.Values.Where(b => results.Any(r => r.Query == b.Query && r.Skipped is null)).ToList()))}");
        }
        return failUnder is { } floor && score.Mrr < floor ? 1 : 0;
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
    /// Where <paramref name="expect"/> comes in <paramref name="titles"/>, from 1; null when it isn't there.
    /// </summary>
    public static int? RankOf(IReadOnlyList<string> titles, string expect)
    {
        for (var i = 0; i < titles.Count; i++)
        {
            if (string.Equals(titles[i], expect, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }
        return null;
    }

    private static async Task<SearchResult> RunAsync(HttpClient http, SearchCase search, CancellationToken ct)
    {
        if (search.Expect is { } expect)
        {
            var suggestions = await EvalHttp.GetAsync<List<Show>>(http, $"api/v1/shows/suggest?q={Uri.EscapeDataString(expect)}", ct)
                ?? throw new CliException($"No catalog at {http.BaseAddress}: is the address right?");
            if (!suggestions.Any(s => string.Equals(s.Title, expect, StringComparison.OrdinalIgnoreCase)))
            {
                return new SearchResult(search.Query, expect, null, 0, [], $"{expect} isn't in the catalog");
            }
        }

        var page = await EvalHttp.GetAsync<Page>(http, $"api/v1/shows?search={Uri.EscapeDataString(search.Query)}&pageSize={PageSize}&import=false", ct)
            ?? throw new CliException($"No catalog at {http.BaseAddress}: is the address right?");
        var titles = page.Items.Select(s => s.Title).ToList();
        return new SearchResult(search.Query, search.Expect, search.Expect is null ? null : RankOf(titles, search.Expect),
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

    private sealed record Show(string Title);

    private sealed record Page(List<Show> Items, int TotalCount, int? TitleCount);
}
