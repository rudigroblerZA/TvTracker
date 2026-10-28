using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Research.API.Data;
using TvTracker.IntegrationEvents;

namespace Research.API.Services;

/// <summary>
/// Checks the catalog's most popular shows against Wikidata, after start and daily, for the research queue: network,
/// premiere date and (for ended shows) episode count. One SPARQL query per 100 shows, found by TVmaze id (P8600) as a
/// gather finds them, so a few queries cover the lot. Each check replaces the table, so a value a moderator fixed (by
/// saving research, which overrides the catalog) drops off the next day.
/// <para>
/// <b>Awards:</b> a second query per batch counts each show's award statements (P166, one per win) and their labels,
/// for the awards shelf; it's separate because counting them in the first query would multiply rows by networks and
/// dates.
/// </para>
/// <para>
/// <b>Wikidata's limits:</b> the query service answers 429 with a Retry-After (1,000 s from the sandbox's shared
/// address), so a batch waits that long (up to half an hour) and tries again, batches are spaced out, and a run that
/// fails is tried again in an hour rather than a day.
/// </para>
/// <list type="bullet">
/// <item><b>Network:</b> differs when no Wikidata network (P449) matches the catalog's: one contains the other, ignoring
/// case and punctuation ("HBO" and "HBO Max"; a show that moved channels lists both on Wikidata), or by initials
/// ("American Broadcasting Company" is ABC, "British Broadcasting Corporation" is in "BBC One"), with "television"
/// read as "TV" ("Fuji Television").</item>
/// <item><b>Premiered:</b> the earliest start (P580) more than a day from the catalog's first air date (a day either way is
/// time zones), at the date's own precision: Wikidata stores a month-only date as the 1st (Chicago Fire's "October
/// 2012"), so it's compared by month, and a year-only one by year.</item>
/// <item><b>Episodes:</b> ended shows only, since Wikidata's count (P1113) lags a running show's. They differ by more than
/// 2 episodes and 10%: specials and double episodes are counted differently.</item>
/// </list>
/// </summary>
public sealed class CatalogChecks(IServiceScopeFactory scopes, TimeProvider time, ILogger<CatalogChecks> logger) : BackgroundService
{
    /// <summary>How many of the catalog's most popular shows are checked (and listed in the queue).</summary>
    public const int Popular = 300;

    private const int BatchSize = 100;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryRun = TimeSpan.FromHours(1);
    private static readonly TimeSpan BetweenBatches = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);
    private const int Attempts = 3;
    private static readonly HttpClient Http = ExternalSources.CreateClient();

    private const string Sparql = """
        SELECT ?tvmaze ?item (GROUP_CONCAT(DISTINCT CONCAT(STR(?start), "@", STR(?precision)); separator="|") AS ?starts)
          (MAX(?episodes) AS ?count)
          (GROUP_CONCAT(DISTINCT ?networkLabel; separator="|") AS ?networks)
        WHERE {
          VALUES ?tvmaze { {IDS} }
          ?item wdt:P8600 ?tvmaze.
          OPTIONAL { ?item p:P580/psv:P580 ?startValue. ?startValue wikibase:timeValue ?start; wikibase:timePrecision ?precision. }
          OPTIONAL { ?item wdt:P1113 ?episodes. }
          OPTIONAL { ?item wdt:P449 ?network. ?network rdfs:label ?networkLabel. FILTER(LANG(?networkLabel) = "en") }
        }
        GROUP BY ?tvmaze ?item
        """;

    /// <param name="Precision">Wikidata's: 11 a day, 10 a month, 9 a year.</param>
    internal sealed record WikidataDate(DateOnly Date, int Precision);

    private sealed record WikidataFacts(string Item, WikidataDate? Started, int? Episodes, IReadOnlyList<string> Networks);

    private sealed record WikidataAwards(string Item, int Wins, IReadOnlyList<string> Labels);

    private const string AwardsSparql = """
        SELECT ?tvmaze ?item (COUNT(?win) AS ?wins) (GROUP_CONCAT(?label; separator="|") AS ?labels)
        WHERE {
          VALUES ?tvmaze { {IDS} }
          ?item wdt:P8600 ?tvmaze; p:P166 ?win.
          ?win ps:P166 ?award.
          ?award rdfs:label ?label. FILTER(LANG(?label) = "en")
        }
        GROUP BY ?tvmaze ?item
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstDelay, time, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = Interval;
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Catalog checks against Wikidata failed; trying again in an hour");
                next = RetryRun;
            }
            await Task.Delay(next, time, stoppingToken);
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogReader>();
        var db = scope.ServiceProvider.GetRequiredService<ResearchContext>();

        var shows = (await catalog.GetPopularFactsAsync(Popular, ct))
            .Select((s, i) => (Show: s, Rank: i + 1))
            .Where(x => x.Show.Source == "tvmaze" && int.TryParse(x.Show.ExternalId, out _))
            .ToList();
        var wikidata = new Dictionary<string, WikidataFacts>();
        var awards = new Dictionary<string, WikidataAwards>();
        foreach (var batch in shows.Chunk(BatchSize))
        {
            var ids = batch.Select(x => x.Show.ExternalId).ToList();
            if (wikidata.Count > 0)
            {
                await Task.Delay(BetweenBatches, time, ct);
            }
            foreach (var (id, facts) in await WithRetryAsync(ids.Count, () => QueryAsync(ids, ct), ct))
            {
                wikidata[id] = facts;
            }
            await Task.Delay(BetweenBatches, time, ct);
            foreach (var (id, won) in await WithRetryAsync(ids.Count, () => QueryAwardsAsync(ids, ct), ct))
            {
                awards[id] = won;
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        var checks = new List<CatalogCheck>();
        foreach (var (show, rank) in shows)
        {
            if (!wikidata.TryGetValue(show.ExternalId, out var w))
            {
                continue;
            }
            void Differs(string field, string? catalogValue, string wikidataValue) => checks.Add(new CatalogCheck
            {
                ShowId = show.Id, Field = field, ShowTitle = show.Title, CatalogValue = catalogValue, WikidataValue = wikidataValue,
                WikidataUrl = w.Item, Rank = rank, CheckedAtUtc = now
            });

            if (w.Networks.Count > 0 && !w.Networks.Any(n => SameNetwork(n, show.Network)))
            {
                Differs("network", show.Network, string.Join(", ", w.Networks));
            }
            if (w.Started is { } started && !SameStart(started, show.FirstAirDate))
            {
                Differs("premiered", show.FirstAirDate?.ToString("yyyy-MM-dd"), started.Precision switch
                {
                    >= 11 => started.Date.ToString("yyyy-MM-dd"),
                    10 => started.Date.ToString("yyyy-MM"),
                    _ => started.Date.ToString("yyyy")
                });
            }
            if (show.Status is ShowStatus.Ended or ShowStatus.Cancelled && w.Episodes is { } count
                && Math.Abs(count - show.AiredEpisodes) > Math.Max(2, count * 0.1))
            {
                Differs("episodes", show.AiredEpisodes.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture));
            }
        }

        var shelf = shows
            .Where(x => awards.TryGetValue(x.Show.ExternalId, out var a) && a.Wins > 0)
            .Select(x => (x.Show, Won: awards[x.Show.ExternalId]))
            .Select(x => new ShowAward
            {
                ShowId = x.Show.Id, ShowTitle = x.Show.Title, PosterUrl = x.Show.PosterUrl, Wins = x.Won.Wins,
                Summary = WikimediaSource.Awards(x.Won.Labels), WikidataUrl = x.Won.Item, CheckedAtUtc = now
            })
            .ToList();

        // Replaced in one transaction, run through the retrying execution strategy as one retriable unit.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.CatalogChecks.ExecuteDeleteAsync(ct);
            db.CatalogChecks.AddRange(checks);
            await db.ShowAwards.ExecuteDeleteAsync(ct);
            db.ShowAwards.AddRange(shelf);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
        logger.LogInformation("Checked {Shows} popular shows against Wikidata ({Found} found there): {Differences} differences, {Awarded} with awards",
            shows.Count, wikidata.Count, checks.Count, shelf.Count);
    }

    /// <summary>A batch's query, waiting out a 429's Retry-After (up to <see cref="MaxWait"/>) between tries.</summary>
    private async Task<T> WithRetryAsync<T>(int shows, Func<Task<T>> query, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await query();
            }
            catch (WikidataBusyException busy) when (attempt < Attempts)
            {
                var wait = busy.RetryAfter is { } after && after < MaxWait ? after : MaxWait;
                logger.LogInformation("Wikidata is busy; checking the next {Count} shows in {Minutes:0} minutes", shows, wait.TotalMinutes);
                await Task.Delay(wait, time, ct);
            }
        }
    }

    private static async Task<Dictionary<string, WikidataAwards>> QueryAwardsAsync(IEnumerable<string> tvmazeIds, CancellationToken ct)
    {
        var found = new Dictionary<string, WikidataAwards>();
        foreach (var row in await SparqlAsync(AwardsSparql, tvmazeIds, ct))
        {
            string? Get(string name) => row.TryGetProperty(name, out var v) && v.GetProperty("value").GetString() is { Length: > 0 } s ? s : null;
            if (Get("tvmaze") is { } id && Get("item") is { } item && int.TryParse(Get("wins"), out var wins))
            {
                found.TryAdd(id, new WikidataAwards(item.Replace("http://www.wikidata.org/entity/", "https://www.wikidata.org/wiki/"), wins,
                    (Get("labels") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
        }
        return found;
    }

    /// <summary>A SPARQL query over a batch of TVmaze ids (<c>{IDS}</c>), its result rows.</summary>
    private static async Task<List<JsonElement>> SparqlAsync(string sparql, IEnumerable<string> tvmazeIds, CancellationToken ct)
    {
        var values = string.Join(' ', tvmazeIds.Select(id => $"\"{id}\""));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://query.wikidata.org/sparql")
        {
            Content = new FormUrlEncodedContent([new("query", sparql.Replace("{IDS}", values))])
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new WikidataBusyException(response.Headers.RetryAfter?.Delta);
        }
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray().Select(r => r.Clone()).ToList();
    }

    private sealed class WikidataBusyException(TimeSpan? retryAfter) : Exception("Wikidata answered 429 Too Many Requests.")
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private static async Task<Dictionary<string, WikidataFacts>> QueryAsync(IEnumerable<string> tvmazeIds, CancellationToken ct)
    {
        var rows = await SparqlAsync(Sparql, tvmazeIds, ct);
        var found = new Dictionary<string, WikidataFacts>();
        foreach (var row in rows)
        {
            string? Get(string name) => row.TryGetProperty(name, out var v) && v.GetProperty("value").GetString() is { Length: > 0 } s ? s : null;
            if (Get("tvmaze") is not { } id || Get("item") is not { } item)
            {
                continue;
            }
            // Two items with one TVmaze id (a show and its franchise) can't be told apart here: keep the first.
            found.TryAdd(id, new WikidataFacts(
                item.Replace("http://www.wikidata.org/entity/", "https://www.wikidata.org/wiki/"),
                Earliest(Get("starts")),
                double.TryParse(Get("count"), CultureInfo.InvariantCulture, out var count) ? (int)count : null,
                (Get("networks") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }
        return found;
    }

    /// <summary>The earliest of Wikidata's starts ("2012-10-01T00:00:00Z@10|…"), with its precision.</summary>
    private static WikidataDate? Earliest(string? starts) =>
        (starts ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split('@'))
            .Where(p => p.Length == 2 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out _))
            .Select(p => new WikidataDate(DateOnly.FromDateTime(DateTime.Parse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)),
                int.TryParse(p[1], out var precision) ? precision : 11))
            .OrderBy(d => d.Date)
            .FirstOrDefault();

    /// <summary>The same start at Wikidata's precision: within a day, the same month, or the same year.</summary>
    internal static bool SameStart(WikidataDate wikidata, DateOnly? catalog) => catalog is { } aired && wikidata.Precision switch
    {
        >= 11 => Math.Abs(aired.DayNumber - wikidata.Date.DayNumber) <= 1,
        10 => aired.Year == wikidata.Date.Year && aired.Month == wikidata.Date.Month,
        _ => aired.Year == wikidata.Date.Year
    };

    private static readonly HashSet<string> MinorWords = new(StringComparer.OrdinalIgnoreCase) { "the", "of", "and" };

    /// <summary>
    /// The same network: one name contains the other, ignoring case and punctuation ("HBO" and "HBO Max", "Apple TV+"
    /// and "Apple TV"), or one's initials are the other's or start it ("American Broadcasting Company" and "ABC",
    /// "British Broadcasting Corporation" and "BBC One"). "Television" counts as "TV".
    /// </summary>
    internal static bool SameNetwork(string wikidata, string? catalog)
    {
        static string Letters(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        static string Plain(string name) => System.Text.RegularExpressions.Regex.Replace(name, "television", "TV",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static string Initials(string name) => new(name.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !MinorWords.Contains(w) && char.IsLetter(w[0])).Select(w => char.ToLowerInvariant(w[0])).ToArray());

        var (a, b) = (Letters(Plain(wikidata)), Letters(Plain(catalog ?? "")));
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }
        if (a.Contains(b) || b.Contains(a))
        {
            return true;
        }
        var (ia, ib) = (Initials(Plain(wikidata)), Initials(Plain(catalog!)));
        return ia.Length >= 2 && b.StartsWith(ia) || ib.Length >= 2 && a.StartsWith(ib);
    }
}
