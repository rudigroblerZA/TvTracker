using System.Reflection;
using System.Text.Json;
using Catalog.API.Services;
using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;
using OpenFeature;
using TvTracker;

namespace Catalog.API.Apis;

public record OpsStats(DateTime GeneratedAtUtc, CatalogOpsStats Catalog, JsonElement? Analytics, JsonElement? BulkImport);

public record CatalogOpsStats(
    int Shows,
    IReadOnlyDictionary<string, int> ShowsByStatus,
    int Seasons,
    int Episodes,
    int UpcomingEpisodes,
    int People,
    int PeopleWithDetails,
    int CastCredits,
    SemanticIndexStats SemanticIndex,
    int ShowsDueForRefresh,
    DateTime? LastSyncedAtUtc,
    IReadOnlyList<OpsGenre> TopGenres,
    IReadOnlyList<OpsRecentShow> RecentShows,
    IReadOnlyList<OpsActor> TopActors,
    OpsBackfills Backfills,
    OpsNanogenres Nanogenres,
    OpsUniverses? Universes,
    IReadOnlyList<OpsFlag> Flags);

/// <summary>The sync worker's slow background fetches from the provider, and how far through they are.</summary>
/// <param name="Actors">People in a catalog show's cast with a provider id: the ones the cast-credit backfill fetches.</param>
/// <param name="ActorsWithCredits">Of those, fetched within the month (the reunion radar's data).</param>
/// <param name="ShowsWithCrew">Shows whose creators and producers have been imported.</param>
/// <param name="PeopleWithCareers">People whose career figures (shows created, produced) have been fetched.</param>
public record OpsBackfills(int Actors, int ActorsWithCredits, int ProviderCastCredits, DateTime? LastCastCreditsAtUtc,
    int ShowsWithCrew, int PeopleWithCareers);

/// <param name="Clusters">Found by clustering; the rest are small genre pairings.</param>
public record OpsNanogenres(int Count, int Clusters, int ShowsCovered, DateTime? ComputedAtUtc, IReadOnlyList<OpsNamedCount> Largest);

/// <summary>Null until the universe map has been built (it's cached for 30 minutes).</summary>
public record OpsUniverses(int Count, int ShowsCovered, IReadOnlyList<OpsNamedCount> Largest);

public record OpsNamedCount(string Name, int Count);

/// <param name="On">Evaluated without a user, so a flag targeting particular users shows its default here.</param>
public record OpsFlag(string Key, bool On);

/// <param name="Current">Shows embedded with the current model since their last sync.</param>
/// <param name="Stale">Embedded, but synced since or by another model; the sync worker redoes these within a minute.</param>
public record SemanticIndexStats(string Model, int Current, int Stale, int Missing, DateTime? LastEmbeddedAtUtc);

public record OpsGenre(string Genre, int Shows);

public record OpsRecentShow(Guid Id, string Title, string? PosterUrl, string? Network, string Status, DateTime AddedAtUtc, bool Indexed);

public record OpsActor(Guid Id, string Name, string? PhotoUrl, int Shows, IReadOnlyList<string> ShowTitles);

/// <summary>
/// A developer dashboard at /ops: what the catalog holds, how much of it is indexed for semantic search, the background
/// backfills, nanogenres and universes, what Analytics has computed, the feature flags, the busiest actors, and the bulk
/// import job. It refreshes itself every 10 seconds.
/// Development only: it isn't signed in and the mobile gateway forwards /catalog/** here.
/// </summary>
public static class OpsApi
{
    public static void MapOpsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ops", () => Results.Text(OpsPage.Html, "text/html; charset=utf-8")).ExcludeFromDescription();
        app.MapGet("/ops/stats", GetStatsAsync);
    }

    private static async Task<OpsStats> GetStatsAsync(
        CatalogContext db, EmbeddingModel model, Universes universes, IFeatureClient features, IHttpClientFactory httpFactory, TimeProvider time,
        ILogger<OpsStats> logger, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // The other services' sections are optional: if one is down the page shows the rest.
        var analytics = FetchAsync(httpFactory, "analytics", "api/v1/ops/stats", logger, ct);
        var bulkImport = FetchAsync(httpFactory, "catalog-sync", "api/v1/imports/bulk/current", logger, ct);
        var catalog = await GetCatalogStatsAsync(db, model.Id, universes, features, now, ct);

        return new OpsStats(now.UtcDateTime, catalog, await analytics, await bulkImport);
    }

    private static async Task<CatalogOpsStats> GetCatalogStatsAsync(
        CatalogContext db, string model, Universes universes, IFeatureClient features, DateTimeOffset now, CancellationToken ct)
    {
        var shows = await db.Shows.CountAsync(ct);
        var current = await db.ShowEmbeddings
            .CountAsync(e => e.Model == model && db.Shows.Any(s => s.Id == e.ShowId && e.EmbeddedAtUtc >= s.LastSyncedAtUtc), ct);
        var embedded = await db.ShowEmbeddings.CountAsync(ct);

        var topActors = await db.Cast
            .GroupBy(c => c.PersonId)
            .Select(g => new { PersonId = g.Key, Shows = g.Count() })
            .OrderByDescending(g => g.Shows)
            .Take(10)
            .ToListAsync(ct);
        var actorIds = topActors.Select(a => a.PersonId).ToList();
        var people = await db.People.Where(p => actorIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var credits = await db.Cast
            .Where(c => actorIds.Contains(c.PersonId))
            .Join(db.Shows, c => c.ShowId, s => s.Id, (c, s) => new { c.PersonId, s.Title, s.Popularity })
            .ToListAsync(ct);

        var indexedIds = db.ShowEmbeddings.Where(e => e.Model == model).Select(e => e.ShowId);

        return new CatalogOpsStats(
            Shows: shows,
            ShowsByStatus: await db.Shows.GroupBy(s => s.Status).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key.ToString(), g => g.Count, ct),
            Seasons: await db.Seasons.CountAsync(ct),
            Episodes: await db.Episodes.CountAsync(ct),
            UpcomingEpisodes: await db.Episodes.CountAsync(e => e.AirsAtUtc > now, ct),
            People: await db.People.CountAsync(ct),
            PeopleWithDetails: await db.People.CountAsync(p => p.ExternalId != null, ct),
            CastCredits: await db.Cast.CountAsync(ct),
            SemanticIndex: new SemanticIndexStats(model, current, embedded - current, shows - embedded,
                await db.ShowEmbeddings.MaxAsync(e => (DateTime?)e.EmbeddedAtUtc, ct)),
            ShowsDueForRefresh: await db.Shows.CountAsync(s => s.NextRefreshAtUtc <= now.UtcDateTime, ct),
            LastSyncedAtUtc: await db.Shows.MaxAsync(s => (DateTime?)s.LastSyncedAtUtc, ct),
            // EF can't group the elements of an array column, so unnest it in SQL.
            TopGenres: await db.Database
                .SqlQueryRaw<OpsGenre>(
                    "SELECT genre AS \"Genre\", COUNT(*)::int AS \"Shows\" FROM shows, unnest(\"Genres\") AS genre " +
                    "GROUP BY genre ORDER BY 2 DESC, 1 LIMIT 8")
                .ToListAsync(ct),
            RecentShows: await db.Shows
                .OrderByDescending(s => s.CreatedAtUtc)
                .Take(8)
                .Select(s => new OpsRecentShow(s.Id, s.Title, s.PosterUrl, s.Network, s.Status.ToString(), s.CreatedAtUtc, indexedIds.Contains(s.Id)))
                .ToListAsync(ct),
            TopActors: topActors
                .Where(a => people.ContainsKey(a.PersonId))
                .Select(a => new OpsActor(a.PersonId, people[a.PersonId].Name, people[a.PersonId].PhotoUrl, a.Shows,
                    credits.Where(c => c.PersonId == a.PersonId).OrderByDescending(c => c.Popularity).Select(c => c.Title).Take(4).ToList()))
                .ToList(),
            Backfills: await GetBackfillsAsync(db, now, ct),
            Nanogenres: new OpsNanogenres(
                await db.Nanogenres.CountAsync(ct),
                await db.Nanogenres.CountAsync(n => n.Source == "cluster", ct),
                await db.Shows.CountAsync(s => s.Nanogenres.Count > 0, ct),
                await db.Nanogenres.MaxAsync(n => (DateTime?)n.ComputedAtUtc, ct),
                await db.Nanogenres.OrderByDescending(n => n.ShowCount).ThenBy(n => n.Name).Take(6)
                    .Select(n => new OpsNamedCount(n.Name, n.ShowCount)).ToListAsync(ct)),
            Universes: await GetUniversesAsync(db, universes, ct),
            Flags: await GetFlagsAsync(features, ct));
    }

    private static async Task<OpsBackfills> GetBackfillsAsync(CatalogContext db, DateTimeOffset now, CancellationToken ct)
    {
        var monthAgo = now.UtcDateTime.AddDays(-30);
        var actors = db.People.Where(p => p.ExternalId != null && db.Cast.Any(c => c.PersonId == p.Id));
        return new OpsBackfills(
            await actors.CountAsync(ct),
            await actors.CountAsync(p => p.CastCreditsRefreshedAtUtc >= monthAgo, ct),
            await db.CastCredits.CountAsync(ct),
            await db.People.MaxAsync(p => p.CastCreditsRefreshedAtUtc, ct),
            await db.Shows.CountAsync(s => s.CreativesImported, ct),
            await db.People.CountAsync(p => p.CareerRefreshedAtUtc != null, ct));
    }

    /// <summary>The universe map's size and largest universes; null if it can't be built right now.</summary>
    private static async Task<OpsUniverses?> GetUniversesAsync(CatalogContext db, Universes universes, CancellationToken ct)
    {
        var map = await universes.GetAsync(ct);
        var largest = map.Universes.OrderByDescending(u => u.Shows.Count).Take(6).ToList();
        var ids = largest.SelectMany(u => u.Shows).ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        return new OpsUniverses(
            map.Universes.Count,
            map.UniverseOfShow.Count,
            largest
                .Where(u => u.Shows.All(shows.ContainsKey))
                .Select(u =>
                {
                    var members = u.Shows.Select(id => shows[id]).ToList();
                    return new OpsNamedCount(UniverseApi.Name(members, UniverseApi.Hub(members, u.Links)), members.Count);
                })
                .ToList());
    }

    /// <summary>Every flag in <see cref="FeatureFlags"/>, as flagd answers for a signed-out caller.</summary>
    private static async Task<List<OpsFlag>> GetFlagsAsync(IFeatureClient features, CancellationToken ct)
    {
        var keys = typeof(FeatureFlags).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        var flags = new List<OpsFlag>();
        foreach (var key in keys)
        {
            flags.Add(new OpsFlag(key, await features.GetBooleanValueAsync(key, FeatureFlags.DefaultValue, cancellationToken: ct)));
        }
        return flags;
    }

    /// <summary>The JSON another service returns, or null when it's down, slow, or has nothing (404).</summary>
    private static async Task<JsonElement?> FetchAsync(
        IHttpClientFactory httpFactory, string client, string path, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var response = await httpFactory.CreateClient(client).GetAsync(path, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Ops dashboard: {Client} unavailable", client);
            return null;
        }
    }
}
