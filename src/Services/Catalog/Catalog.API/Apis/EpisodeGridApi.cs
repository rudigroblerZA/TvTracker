using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Seasons">Regular seasons in order, each with its episodes in order.</param>
/// <param name="Best">The highest-rated episode (the earliest of a tie).</param>
/// <param name="Weakest">The lowest-rated episode, when the show has <see cref="EpisodeGridApi.MinRated"/>+ rated.</param>
/// <param name="BestRun">The best <see cref="EpisodeGridApi.RunLength"/> episodes in a row by average rating, unbroken by an
/// unrated one.</param>
public record EpisodeGridDto(Guid ShowId, IReadOnlyList<GridSeasonDto> Seasons, GridCellDto? Best, GridCellDto? Weakest, GridRunDto? BestRun);

/// <param name="Average">The mean of its rated episodes, from three up.</param>
public record GridSeasonDto(int Number, double? Average, IReadOnlyList<GridCellDto> Episodes);

/// <param name="Rating">The provider's rating, null when it has none (unaired, or too few votes).</param>
public record GridCellDto(Guid EpisodeId, int Season, int Number, string Title, double? Rating, bool Aired);

/// <param name="From">Its first episode.</param>
/// <param name="To">Its last.</param>
public record GridRunDto(GridCellDto From, GridCellDto To, double Average);

/// <summary>
/// The episode grid: every regular episode of a show by season, with the provider's rating, the way fans chart a show's
/// run (one row per season, one square per episode). The best and weakest episodes and the best run of
/// <see cref="RunLength"/> are marked, since a grid of a few hundred squares is hard to read alone.
/// <list type="bullet">
/// <item><b>Regular seasons only;</b> specials (season 0) have no place in a row.</item>
/// <item><b>Daily shows</b> (a season over <see cref="DailySeason"/> episodes) get none, as Episode lengths and Between
/// seasons leave them out: a row of 200 dated episodes is a barcode, not a chart.</item>
/// <item><b>Titles travel with the cells;</b> the page shows them only for episodes the viewer has watched or with the
/// spoiler shield off, as it does every title.</item>
/// </list>
/// One query per show, cached <see cref="CacheFor"/>: ratings move slowly, and a refresh brings new ones within that.
/// </summary>
public static class EpisodeGridApi
{
    public const int MinRated = 6;
    public const int RunLength = 5;
    private const int DailySeason = 60;
    private const int MinSeasonRated = 3;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    public static void MapEpisodeGridApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/{id:guid}/episode-grid", GetAsync).RequireFeature(FeatureFlags.EpisodeGrid);

    private static async Task<Results<Ok<EpisodeGridDto>, NotFound>> GetAsync(Guid id, CatalogContext db, HybridCache cache, TimeProvider time,
        CancellationToken ct)
    {
        var grid = await cache.GetOrCreateAsync($"episode-grid:{id}", async token =>
        {
            var now = time.GetUtcNow();
            var cells = await db.Episodes.AsNoTracking()
                .Where(e => e.ShowId == id && e.SeasonNumber > 0)
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .Select(e => new GridCellDto(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title, e.ExternalRating,
                    e.AirsAtUtc != null && e.AirsAtUtc <= now))
                .ToListAsync(token);
            return Build(id, cells);
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);
        return grid is null ? TypedResults.NotFound() : TypedResults.Ok(grid);
    }

    /// <summary>The grid, or null when there's nothing to chart: too few rated episodes, or a daily show.</summary>
    internal static EpisodeGridDto? Build(Guid showId, List<GridCellDto> cells)
    {
        var rated = cells.Where(c => c.Rating is not null).ToList();
        var seasons = cells.GroupBy(c => c.Season).ToList();
        if (rated.Count < MinRated || seasons.Any(s => s.Count() > DailySeason))
        {
            return null;
        }

        var best = rated.MaxBy(c => c.Rating!.Value);
        var weakest = rated.MinBy(c => c.Rating!.Value);

        // Runs follow air order across seasons (a season's finale into the next premiere), and an unrated episode breaks one.
        GridRunDto? run = null;
        for (var i = 0; i + RunLength <= cells.Count; i++)
        {
            var window = cells.Skip(i).Take(RunLength).ToList();
            if (window.Any(c => c.Rating is null))
            {
                continue;
            }
            var average = Math.Round(window.Average(c => c.Rating!.Value), 2);
            if (run is null || average > run.Average)
            {
                run = new GridRunDto(window[0], window[^1], average);
            }
        }

        return new EpisodeGridDto(showId,
            seasons.Select(s => new GridSeasonDto(s.Key,
                s.Count(c => c.Rating is not null) >= MinSeasonRated ? Math.Round(s.Where(c => c.Rating is not null).Average(c => c.Rating!.Value), 1) : null,
                s.ToList())).ToList(),
            best, best?.EpisodeId == weakest?.EpisodeId ? null : weakest, run);
    }
}
