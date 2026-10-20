using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <summary>
/// Read-only catalog endpoints. Catalog data is public, so these don't require sign-in.
/// The sync worker writes the data and removes cache entries when shows change.
/// </summary>
public static class CatalogApi
{
    public static RouteGroupBuilder MapCatalogApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/shows", SearchShowsAsync).RequireRateLimiting(SearchRateLimiting.Policy);
        api.MapGet("/shows/trending", GetTrendingAsync);
        api.MapGet("/shows/batch", GetShowsByIdsAsync);
        api.MapGet("/shows/{id:guid}", GetShowAsync);
        api.MapGet("/shows/{id:guid}/seasons/{number:int}", GetSeasonAsync);
        api.MapGet("/episodes/{id:guid}", GetEpisodeAsync);
        api.MapGet("/schedule", GetScheduleAsync);
        api.MapGet("/genres", GetGenresAsync);
        api.MapGet("/genres/{genre}/shows", GetShowsByGenreAsync);

        return api;
    }

    private static async Task<Ok<PagedResult<ShowSummaryDto>>> SearchShowsAsync(
        CatalogContext db, CatalogImportClient importer, string? search, string? genre, ShowStatus? status,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (page <= 1 && !string.IsNullOrWhiteSpace(search))
        {
            // Pulls in matching shows the catalog doesn't have yet, so they appear in the results.
            await importer.ImportSearchMatchesAsync(search, ct);
        }

        return await QueryShowsAsync(db, search, genre, status, page, pageSize, ct);
    }

    private static async Task<Ok<PagedResult<ShowSummaryDto>>> QueryShowsAsync(
        CatalogContext db, string? search, string? genre, ShowStatus? status, int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize) = Normalize(page, pageSize);

        var query = db.Shows.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(s => EF.Functions.ILike(s.Title, pattern));
        }
        if (!string.IsNullOrWhiteSpace(genre))
        {
            query = query.Where(s => s.Genres.Contains(genre));
        }
        if (status is not null)
        {
            query = query.Where(s => s.Status == status);
        }

        var total = await query.CountAsync(ct);
        var shows = await query
            .OrderByDescending(s => s.Popularity)
            .ThenBy(s => s.Title)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return TypedResults.Ok(new PagedResult<ShowSummaryDto>(shows.Select(s => s.ToSummary()).ToList(), page, pageSize, total));
    }

    private static async Task<Ok<List<ShowSummaryDto>>> GetTrendingAsync(CatalogContext db, HybridCache cache, CancellationToken ct)
    {
        var trending = await cache.GetOrCreateAsync(
            CacheKeys.Trending,
            async token =>
            {
                var shows = await db.Shows.AsNoTracking()
                    .OrderByDescending(s => s.Popularity)
                    .Take(20)
                    .ToListAsync(token);
                return shows.Select(s => s.ToSummary()).ToList();
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = TimeSpan.FromMinutes(1) },
            cancellationToken: ct);

        return TypedResults.Ok(trending);
    }

    /// <summary>Summaries for a set of show ids (comma-separated), e.g. to render recommendations or lists.</summary>
    private static async Task<Ok<List<ShowSummaryDto>>> GetShowsByIdsAsync(CatalogContext db, string ids, CancellationToken ct)
    {
        var showIds = ParseIds(ids);
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.Id)).ToListAsync(ct);

        // Keep the caller's order.
        var ordered = showIds
            .Select(id => shows.FirstOrDefault(s => s.Id == id))
            .OfType<Show>()
            .Select(s => s.ToSummary())
            .ToList();

        return TypedResults.Ok(ordered);
    }

    private static async Task<Results<Ok<ShowDetailDto>, NotFound>> GetShowAsync(
        Guid id, CatalogContext db, HybridCache cache, string? region, TimeProvider time, CancellationToken ct)
    {
        region = string.IsNullOrWhiteSpace(region) ? "US" : region.ToUpperInvariant();

        var detail = await cache.GetOrCreateAsync(
            CacheKeys.Show(id, region),
            async token => await LoadShowAsync(id, region, db, time, token),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = TimeSpan.FromMinutes(1) },
            tags: [CacheKeys.ShowTag(id)],
            cancellationToken: ct);

        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<ShowDetailDto?> LoadShowAsync(Guid id, string region, CatalogContext db, TimeProvider time, CancellationToken ct)
    {
        var show = await db.Shows.AsNoTracking()
            .Include(s => s.Seasons)
            .Include(s => s.Episodes)
            .Include(s => s.Cast).ThenInclude(c => c.Person)
            .Include(s => s.WatchProviders.Where(w => w.Region == region))
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == id, ct);

        if (show is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var bySeason = show.Episodes.GroupBy(e => e.SeasonNumber).ToDictionary(g => g.Key, g => g.ToList());

        EpisodeDto Labeled(Episode e) => e.ToDto(CatalogMappings.LabelFor(e, bySeason[e.SeasonNumber]));

        var next = show.Episodes
            .Where(e => e.AirsAtUtc > now)
            .OrderBy(e => e.AirsAtUtc)
            .FirstOrDefault();
        var last = show.Episodes
            .Where(e => e.AirsAtUtc <= now)
            .OrderByDescending(e => e.AirsAtUtc)
            .FirstOrDefault();

        return new ShowDetailDto(
            show.Id,
            show.Title,
            show.Overview,
            show.PosterUrl,
            show.BackdropUrl,
            show.FirstAirDate,
            show.Status,
            show.Network,
            show.Genres,
            show.Seasons
                .OrderBy(s => s.Number)
                .Select(s => new SeasonSummaryDto(s.Number, s.Name, bySeason.GetValueOrDefault(s.Number)?.Count ?? 0, s.PremieresAtUtc))
                .ToList(),
            show.Cast
                .OrderBy(c => c.Order)
                .Select(c => new CastMemberDto(c.Person.Name, c.Character, c.Person.PhotoUrl))
                .ToList(),
            region,
            show.WatchProviders
                .OrderBy(w => w.Type).ThenBy(w => w.ProviderName)
                .Select(w => new WatchProviderDto(w.ProviderName, w.Type, w.Url))
                .ToList(),
            next is null ? null : Labeled(next),
            last is null ? null : Labeled(last));
    }

    private static async Task<Results<Ok<SeasonDetailDto>, NotFound>> GetSeasonAsync(
        Guid id, int number, CatalogContext db, CancellationToken ct)
    {
        var season = await db.Seasons.AsNoTracking().SingleOrDefaultAsync(s => s.ShowId == id && s.Number == number, ct);
        if (season is null)
        {
            return TypedResults.NotFound();
        }

        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => e.ShowId == id && e.SeasonNumber == number)
            .OrderBy(e => e.EpisodeNumber)
            .ToListAsync(ct);

        return TypedResults.Ok(new SeasonDetailDto(
            id, season.Number, season.Name, season.Overview, season.PremieresAtUtc,
            episodes.Select(e => e.ToDto(CatalogMappings.LabelFor(e, episodes))).ToList()));
    }

    private static async Task<Results<Ok<EpisodeDto>, NotFound>> GetEpisodeAsync(Guid id, CatalogContext db, CancellationToken ct)
    {
        var episode = await db.Episodes.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null)
        {
            return TypedResults.NotFound();
        }

        var season = await db.Episodes.AsNoTracking()
            .Where(e => e.ShowId == episode.ShowId && e.SeasonNumber == episode.SeasonNumber)
            .ToListAsync(ct);

        return TypedResults.Ok(episode.ToDto(CatalogMappings.LabelFor(episode, season)));
    }

    /// <summary>Episodes airing between <paramref name="from"/> and <paramref name="to"/> (UTC), optionally for given shows.</summary>
    private static async Task<Results<Ok<List<ScheduleItemDto>>, BadRequest<string>>> GetScheduleAsync(
        CatalogContext db, DateTimeOffset from, DateTimeOffset to, string? showIds, CancellationToken ct)
    {
        if (to <= from || to - from > TimeSpan.FromDays(62))
        {
            return TypedResults.BadRequest("'to' must be after 'from' and the range at most 62 days.");
        }

        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();

        var query = db.Episodes.AsNoTracking().Where(e => e.AirsAtUtc >= fromUtc && e.AirsAtUtc < toUtc);
        if (!string.IsNullOrWhiteSpace(showIds))
        {
            var ids = ParseIds(showIds);
            query = query.Where(e => ids.Contains(e.ShowId));
        }

        var items = await query
            .OrderBy(e => e.AirsAtUtc)
            .Join(db.Shows, e => e.ShowId, s => s.Id, (e, s) => new { Episode = e, s.Title, s.PosterUrl })
            .Take(500)
            .ToListAsync(ct);

        return TypedResults.Ok(items
            .Select(i => new ScheduleItemDto(i.Episode.ShowId, i.Title, i.PosterUrl, i.Episode.ToDto()))
            .ToList());
    }

    private static async Task<Ok<List<string>>> GetGenresAsync(CatalogContext db, CancellationToken ct)
    {
        var genres = await db.Shows.SelectMany(s => s.Genres).Distinct().OrderBy(g => g).ToListAsync(ct);
        return TypedResults.Ok(genres);
    }

    private static Task<Ok<PagedResult<ShowSummaryDto>>> GetShowsByGenreAsync(
        string genre, CatalogContext db, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        QueryShowsAsync(db, search: null, genre, status: null, page, pageSize, ct);

    private static (int Page, int PageSize) Normalize(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));

    private static List<Guid> ParseIds(string ids) =>
        ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .Take(200)
            .ToList();
}
