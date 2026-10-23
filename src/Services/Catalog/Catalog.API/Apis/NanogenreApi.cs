using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Posters">Posters of its most popular shows, for the index.</param>
public record NanogenreDto(string Slug, string Name, int ShowCount, IReadOnlyList<string> Posters);

/// <param name="Source">"cluster" (found by clustering) or "genres" (a small group sharing a genre pairing).</param>
public record NanogenreDetailDto(string Slug, string Name, string Source, IReadOnlyList<ShowSummaryDto> Shows);

/// <summary>
/// Nanogenres: fine-grained genres the sync worker discovers in the catalog (see Catalog.SyncWorker's
/// NanogenreDiscovery). Public, like the catalog: they come from shows' genres and descriptions, not from anyone's data.
/// </summary>
public static class NanogenreApi
{
    private const int PostersPerNanogenre = 4;

    public static void MapNanogenreApi(this RouteGroupBuilder api)
    {
        api.MapGet("/nanogenres", GetAllAsync).RequireFeature(FeatureFlags.Nanogenres);
        api.MapGet("/nanogenres/{slug}", GetAsync).RequireFeature(FeatureFlags.Nanogenres);
    }

    /// <summary>
    /// Every nanogenre, largest first. Cached for <see cref="CacheFor"/> (they're rediscovered every 6 hours), and the
    /// posters are picked in SQL, so a page view doesn't read every show.
    /// </summary>
    private static async Task<Ok<List<NanogenreDto>>> GetAllAsync(CatalogContext db, HybridCache cache, CancellationToken ct) =>
        TypedResults.Ok(await cache.GetOrCreateAsync("catalog:nanogenres", async token =>
            {
                var nanogenres = await db.Nanogenres.AsNoTracking().OrderByDescending(n => n.ShowCount).ThenBy(n => n.Name).ToListAsync(token);
                var posters = (await db.Database
                        .SqlQueryRaw<NanogenrePoster>(
                            "SELECT slug AS \"Slug\", \"PosterUrl\" FROM (" +
                            "SELECT n.slug, s.\"PosterUrl\", row_number() OVER (PARTITION BY n.slug ORDER BY s.\"Popularity\" DESC) AS rank " +
                            "FROM shows s CROSS JOIN LATERAL unnest(s.\"Nanogenres\") AS n(slug) WHERE s.\"PosterUrl\" IS NOT NULL) ranked " +
                            "WHERE rank <= {0} ORDER BY slug, rank", PostersPerNanogenre)
                        .ToListAsync(token))
                    .ToLookup(p => p.Slug, p => p.PosterUrl);

                return nanogenres
                    .Select(n => new NanogenreDto(n.Slug, n.Name, n.ShowCount, posters[n.Slug].ToList()))
                    .ToList();
            },
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor },
            cancellationToken: ct));

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private sealed record NanogenrePoster(string Slug, string PosterUrl);

    /// <summary>A nanogenre's shows, most popular first. 404 for a slug that's gone since the last discovery.</summary>
    private static async Task<Results<Ok<NanogenreDetailDto>, NotFound>> GetAsync(string slug, CatalogContext db, CancellationToken ct)
    {
        if (await db.Nanogenres.AsNoTracking().SingleOrDefaultAsync(n => n.Slug == slug, ct) is not { } nanogenre)
        {
            return TypedResults.NotFound();
        }

        var shows = await db.Shows.AsNoTracking()
            .Where(s => s.Nanogenres.Contains(slug))
            .OrderByDescending(s => s.Popularity)
            .ThenBy(s => s.Title)
            .ToListAsync(ct);
        return TypedResults.Ok(new NanogenreDetailDto(nanogenre.Slug, nanogenre.Name, nanogenre.Source, shows.Select(s => s.ToSummary()).ToList()));
    }

    /// <summary>Names for a show's nanogenre slugs, in the show's order; slugs without a nanogenre any more are skipped.</summary>
    internal static async Task<List<NanogenreRefDto>> RefsAsync(CatalogContext db, List<string> slugs, CancellationToken ct)
    {
        if (slugs.Count == 0)
        {
            return [];
        }

        var names = await db.Nanogenres.AsNoTracking().Where(n => slugs.Contains(n.Slug)).ToDictionaryAsync(n => n.Slug, n => n.Name, ct);
        return slugs.Where(names.ContainsKey).Select(slug => new NanogenreRefDto(slug, names[slug])).ToList();
    }
}
