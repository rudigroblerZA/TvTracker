using System.Net.Http.Json;
using System.Text.Json;

namespace Catalog.ImportCli;

/// <summary>
/// One mix to check: two shows are a blend, three to five a mixtape.
/// </summary>
/// <param name="Shows">The shows' titles, as the catalog has them.</param>
/// <param name="Expect">At least one of these must be among the first <see cref="MixEval.Top"/> matches.</param>
/// <param name="Never">No match among those may have a title that starts with one of these (a spin-off of one of the
/// shows, say).</param>
/// <param name="Loose">Whether the answer should say the shows have little in common; not checked when left out.</param>
/// <param name="Years">The year each of <paramref name="Shows"/> started, in the same order, for telling it from another show
/// with the same title (The Office, Doctor Who); null for a show that needs none, or left out altogether.</param>
public sealed record MixCase(string[] Shows, string[]? Expect = null, string[]? Never = null, bool? Loose = null, int?[]? Years = null)
{
    public string Name => string.Join(Shows.Length == 2 ? " × " : " + ", Shows);
}

/// <param name="Titles">The first <see cref="MixEval.Top"/> matches, best first; empty when the mix couldn't be run.</param>
/// <param name="Skipped">Why it wasn't run (a show the catalog doesn't have, or one not embedded yet).</param>
public sealed record MixResult(string Name, string[] Titles, bool Loose, string[] Failures, string? Skipped = null);

/// <summary>
/// Checks Blend and Mixtape against a fixed list of mixes (docs/design/mix-pairs.json), so a change to how they rank
/// is a comparison and not an opinion: what each mix must and must not return, and, against a saved earlier run, how
/// much of each answer changed. Read-only, through the public endpoints.
/// </summary>
public static class MixEval
{
    public const int Top = 10;

    /// <param name="token">A member's access token, for a mix their plan decides (Mixtape is a Pro insight); none asks signed out.</param>
    public static async Task<int> RunAsync(string catalogUrl, string casesFile, string? outFile, string? baselineFile, string? token, CancellationToken ct)
    {
        var cases = await EvalHttp.ReadAsync<MixCase>(casesFile, ct);
        var baseline = await EvalHttp.BaselineAsync<MixResult>(baselineFile, r => r.Name, ct);

        using var http = EvalHttp.Client(catalogUrl, token);
        var results = new List<MixResult>();
        foreach (var mix in cases)
        {
            var result = await RunAsync(http, mix, ct);
            results.Add(result);
            Console.WriteLine(Describe(result, baseline.GetValueOrDefault(result.Name)));
        }

        await EvalHttp.SaveAsync(outFile, results, ct);

        var ran = results.Where(r => r.Skipped is null).ToList();
        var failed = ran.Count(r => r.Failures.Length > 0);
        Console.WriteLine();
        Console.WriteLine($"{ran.Count - failed} of {ran.Count} passed, {results.Count - ran.Count} skipped.");
        if (baseline.Count > 0 && ran.Where(r => baseline.ContainsKey(r.Name)).Select(r => Kept(r, baseline[r.Name])).ToList() is { Count: > 0 } kept)
        {
            Console.WriteLine($"Against the baseline, {kept.Average():P0} of each answer's first {Top} is unchanged on average.");
        }
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// What an answer gets wrong for its case; empty when it passes.
    /// </summary>
    public static string[] Judge(MixCase mix, IReadOnlyList<string> titles, bool loose)
    {
        var failures = new List<string>();
        if (mix.Expect is { Length: > 0 } expect && !expect.Any(e => titles.Contains(e, StringComparer.OrdinalIgnoreCase)))
        {
            failures.Add($"none of {string.Join(", ", expect)} in the first {Top}");
        }
        foreach (var banned in mix.Never ?? [])
        {
            if (titles.FirstOrDefault(t => t.StartsWith(banned, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                failures.Add($"{found} is in the first {Top}");
            }
        }
        if (mix.Loose is { } expected && expected != loose)
        {
            failures.Add(expected ? "not marked loose" : "marked loose");
        }
        return failures.ToArray();
    }

    /// <summary>
    /// The share of the baseline's answer that is still in this one.
    /// </summary>
    public static double Kept(MixResult now, MixResult before) =>
        before.Titles.Length == 0 ? 1 : before.Titles.Count(t => now.Titles.Contains(t)) / (double)before.Titles.Length;

    private static async Task<MixResult> RunAsync(HttpClient http, MixCase mix, CancellationToken ct)
    {
        if (mix.Shows.Length is < 2 or > 5)
        {
            return new MixResult(mix.Name, [], false, [], "needs two to five shows");
        }

        var ids = new List<Guid>();
        for (var i = 0; i < mix.Shows.Length; i++)
        {
            var title = mix.Shows[i];
            // Two shows with one title and no year to tell them apart: the mix is skipped, not guessed.
            var (id, why) = await EvalHttp.ResolveShowAsync(http, title, mix.Years?.ElementAtOrDefault(i), ct);
            if (id is null)
            {
                return new MixResult(mix.Name, [], false, [], why);
            }
            ids.Add(id.Value);
        }

        var answer = ids.Count == 2
            ? await EvalHttp.GetAsync<Answer>(http, $"api/v1/shows/blend?a={ids[0]}&b={ids[1]}", ct)
            : await EvalHttp.GetAsync<Answer>(http, $"api/v1/shows/mixtape?ids={string.Join(',', ids)}", ct);
        if (answer is null)
        {
            // Off, or a plan's insight asked for without a member on that plan.
            return new MixResult(mix.Name, [], false, [], ids.Count == 2 ? "Blend is off" : "Mixtape is off, or needs --token of a member whose plan has it");
        }
        if (!answer.Indexed)
        {
            return new MixResult(mix.Name, [], false, [], "a show isn't embedded yet");
        }

        var titles = answer.Matches.Take(Top).Select(m => m.Show.Title).ToArray();
        return new MixResult(mix.Name, titles, answer.Loose, Judge(mix, titles, answer.Loose));
    }

    private static string Describe(MixResult result, MixResult? before)
    {
        if (result.Skipped is { } why)
        {
            return $"SKIP  {result.Name}: {why}";
        }

        var line = $"{(result.Failures.Length == 0 ? "PASS" : "FAIL")}  {result.Name}{(result.Loose ? " (loose)" : "")}: {string.Join(", ", result.Titles.Take(5))}";
        foreach (var failure in result.Failures)
        {
            line += $"{Environment.NewLine}      {failure}";
        }
        if (before is { Skipped: null } && Kept(result, before) < 1)
        {
            var gone = before.Titles.Except(result.Titles).ToList();
            var added = result.Titles.Except(before.Titles).ToList();
            line += $"{Environment.NewLine}      changed: -{string.Join(", -", gone)}; +{string.Join(", +", added)}";
        }
        return line;
    }

    private sealed record Suggestion(Guid Id, string Title);

    private sealed record Answer(bool Indexed, bool Loose, List<Match> Matches);

    private sealed record Match(Suggestion Show);
}
