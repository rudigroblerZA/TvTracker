using System.Text.Json;
using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;

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
    IReadOnlyList<OpsActor> TopActors);

/// <param name="Current">Shows embedded with the current model since their last sync.</param>
/// <param name="Stale">Embedded, but synced since or by another model; the sync worker redoes these within a minute.</param>
public record SemanticIndexStats(string Model, int Current, int Stale, int Missing, DateTime? LastEmbeddedAtUtc);

public record OpsGenre(string Genre, int Shows);

public record OpsRecentShow(Guid Id, string Title, string? PosterUrl, string? Network, string Status, DateTime AddedAtUtc, bool Indexed);

public record OpsActor(Guid Id, string Name, string? PhotoUrl, int Shows, IReadOnlyList<string> ShowTitles);

/// <summary>
/// A developer dashboard at /ops: what the catalog holds, how much of it is indexed for semantic search, what
/// Analytics has computed, the busiest actors, and the bulk import job. It refreshes itself every 10 seconds.
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
        CatalogContext db, IHttpClientFactory httpFactory, TimeProvider time, ILogger<OpsStats> logger, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // The other services' sections are optional: if one is down the page shows the rest.
        var analytics = FetchAsync(httpFactory, "analytics", "api/v1/ops/stats", logger, ct);
        var bulkImport = FetchAsync(httpFactory, "catalog-sync", "api/v1/imports/bulk/current", logger, ct);
        var catalog = await GetCatalogStatsAsync(db, now, ct);

        return new OpsStats(now.UtcDateTime, catalog, await analytics, await bulkImport);
    }

    private static async Task<CatalogOpsStats> GetCatalogStatsAsync(CatalogContext db, DateTimeOffset now, CancellationToken ct)
    {
        var model = LocalEmbeddingGenerator.ModelId;
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
                .ToList());
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
