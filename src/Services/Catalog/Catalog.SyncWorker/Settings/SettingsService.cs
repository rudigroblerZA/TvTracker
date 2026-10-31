using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Catalog.Data;
using Catalog.SyncWorker.Quotes;
using Microsoft.EntityFrameworkCore;

namespace Catalog.SyncWorker.Settings;

/// <summary>
/// Where shows are set, for the TV atlas: Wikidata's narrative location (P840) of the item with each show's TVmaze id
/// (P8600, as Research and Memorable lines find shows), kept when the place has coordinates on Earth (The Expanse's Moon
/// has some too). One SPARQL query per <see cref="Batch"/> shows, a batch a round while any are due, most popular first;
/// each show is looked up again monthly, and its rows are replaced with what Wikidata says then.
/// <para>
/// <b>Gently:</b> Wikidata limits shared addresses hard, so a 429 is waited out as asked (up to <see cref="MaxWait"/>),
/// and a failed round is simply tried again in the next. A plain client with one try, as Research's sources.
/// </para>
/// </summary>
public sealed class SettingsService(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<SettingsService> logger) : BackgroundService
{
    private const string Provider = "tvmaze";
    private const int Batch = 100;
    private const int Attempts = 3;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Idle = TimeSpan.FromHours(6);
    private static readonly TimeSpan LookupAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", "TvTracker-atlas/1.0 (+https://github.com/rudigroblerZA/TvTracker)" } }
    };

    // Q2 is Earth: places on other globes have coordinates too.
    private const string Sparql = """
        SELECT ?tvmaze ?place ?placeLabel ?countryLabel ?lat ?lon WHERE {
          VALUES ?tvmaze { {IDS} }
          ?item wdt:P8600 ?tvmaze; wdt:P840 ?place.
          ?place p:P625/psv:P625 ?coord.
          ?coord wikibase:geoLatitude ?lat; wikibase:geoLongitude ?lon; wikibase:geoGlobe wd:Q2.
          ?place rdfs:label ?placeLabel. FILTER(LANG(?placeLabel) = "en")
          OPTIONAL { ?place wdt:P17 ?country. ?country rdfs:label ?countryLabel. FILTER(LANG(?countryLabel) = "en") }
        }
        """;

    private sealed record Place(string ExternalId, string PlaceId, string Name, string? Country, double Latitude, double Longitude);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstDelay, time, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = Idle;
            try
            {
                if (await LookUpAsync(stoppingToken) > 0)
                {
                    next = Round;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "TV atlas: looking up settings failed; trying again in {Round}", Round);
                next = Round;
            }
            await Task.Delay(next, time, stoppingToken);
        }
    }

    /// <summary>One batch of due shows: their settings replaced, and their lookup stamped. Returns how many were due.</summary>
    private async Task<int> LookUpAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var staleBefore = now - LookupAfter;
        var due = await db.Shows.AsNoTracking()
            .Where(s => s.Source == Provider && !db.SettingLookups.Any(l => l.ShowId == s.Id && l.CheckedAtUtc >= staleBefore))
            .OrderByDescending(s => s.Popularity)
            .Select(s => new { s.Id, s.ExternalId })
            .Take(Batch)
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        var places = (await WithRetryAsync(() => QueryAsync(due.Select(s => s.ExternalId), ct), ct)).ToLookup(p => p.ExternalId);
        var ids = due.Select(s => s.Id).ToList();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Settings.Where(s => ids.Contains(s.ShowId)).ExecuteDeleteAsync(ct);
            await db.SettingLookups.Where(l => ids.Contains(l.ShowId)).ExecuteDeleteAsync(ct);
            foreach (var show in due)
            {
                db.Settings.AddRange(places[show.ExternalId].Select(p => new ShowSetting
                {
                    ShowId = show.Id, PlaceId = p.PlaceId, Name = p.Name, Country = p.Country, Latitude = p.Latitude, Longitude = p.Longitude
                }));
                db.SettingLookups.Add(new ShowSettingLookup { ShowId = show.Id, CheckedAtUtc = now });
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
        logger.LogInformation("TV atlas: looked up {Shows} shows, {Set} with a setting ({Places} places)",
            due.Count, due.Count(s => places[s.ExternalId].Any()), places.Sum(p => p.Count()));
        return due.Count;
    }

    /// <summary>Each show's places, one row per place (its first coordinates and country when Wikidata has several).</summary>
    private static async Task<List<Place>> QueryAsync(IEnumerable<string> tvmazeIds, CancellationToken ct)
    {
        var values = string.Join(' ', tvmazeIds.Select(id => $"\"{id}\""));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://query.wikidata.org/sparql")
        {
            Content = new FormUrlEncodedContent([new("query", Sparql.Replace("{IDS}", values))])
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new WikimediaBusyException(response.Headers.RetryAfter?.Delta);
        }
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var places = new List<Place>();
        foreach (var row in json.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray())
        {
            string? Get(string name) => row.TryGetProperty(name, out var v) && v.GetProperty("value").GetString() is { Length: > 0 } s ? s : null;
            if (Get("tvmaze") is { } id && Get("place") is { } place && Get("placeLabel") is { } name
                && double.TryParse(Get("lat"), CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(Get("lon"), CultureInfo.InvariantCulture, out var lon))
            {
                places.Add(new Place(id, place[(place.LastIndexOf('/') + 1)..], name[..Math.Min(name.Length, 256)],
                    Get("countryLabel") is { } country ? country[..Math.Min(country.Length, 128)] : null, lat, lon));
            }
        }
        return places.DistinctBy(p => (p.ExternalId, p.PlaceId)).ToList();
    }

    private async Task<T> WithRetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (WikimediaBusyException busy) when (attempt < Attempts)
            {
                var wait = busy.RetryAfter is { } after && after < MaxWait ? after : TimeSpan.FromMinutes(attempt * 2);
                logger.LogInformation("TV atlas: Wikidata is busy; waiting {Seconds:0} s", wait.TotalSeconds);
                await Task.Delay(wait, time, ct);
            }
        }
    }
}
