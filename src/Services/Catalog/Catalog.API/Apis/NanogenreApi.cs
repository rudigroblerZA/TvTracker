using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Every nanogenre, largest first.</summary>
    private static async Task<Ok<List<NanogenreDto>>> GetAllAsync(CatalogContext db, CancellationToken ct)
    {
        var nanogenres = await db.Nanogenres.AsNoTracking().OrderByDescending(n => n.ShowCount).ThenBy(n => n.Name).ToListAsync(ct);
        var posters = (await db.Shows.AsNoTracking()
                .Where(s => s.Nanogenres.Count > 0 && s.PosterUrl != null)
                .OrderByDescending(s => s.Popularity)
                .Select(s => new { s.Nanogenres, s.PosterUrl })
                .ToListAsync(ct))
            .SelectMany(s => s.Nanogenres.Select(slug => (Slug: slug, Poster: s.PosterUrl!)))
            .ToLookup(x => x.Slug, x => x.Poster);

        return TypedResults.Ok(nanogenres
            .Select(n => new NanogenreDto(n.Slug, n.Name, n.ShowCount, posters[n.Slug].Take(PostersPerNanogenre).ToList()))
            .ToList());
    }

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
