using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Verdict">"longer", "shorter" or "steady": the last counted season's typical episode against the first's.</param>
/// <param name="Jump">The biggest change from one season to the next, when it's <see cref="EpisodeLengthsApi.JumpMinutes"/> or more.</param>
public record EpisodeLengthsDto(Guid ShowId, string Verdict, int FirstMinutes, int LastMinutes, SeasonJumpDto? Jump,
    IReadOnlyList<SeasonLengthDto> Seasons);

/// <param name="Minutes">The season's median episode runtime.</param>
/// <param name="TotalMinutes">All of its aired episodes' runtimes added up.</param>
/// <param name="Counted">It has <see cref="EpisodeLengthsApi.MinEpisodes"/>+ episodes with a runtime, so the verdict uses it.</param>
public record SeasonLengthDto(int Number, int Episodes, int Minutes, int TotalMinutes, bool Counted);

public record SeasonJumpDto(int FromSeason, int ToSeason, int FromMinutes, int ToMinutes);

/// <param name="Longer">Scripted shows whose episodes grew, most popular first.</param>
/// <param name="Shorter">Scripted shows whose episodes shrank, most popular first.</param>
public record EpisodeLengthListsDto(IReadOnlyList<LengthChangeDto> Longer, IReadOnlyList<LengthChangeDto> Shorter);

/// <param name="Change">Last against first, as a share (0.5 is half as long again).</param>
public record LengthChangeDto(Guid ShowId, string Title, string? PosterUrl, int FirstSeason, int FirstMinutes, int LastSeason, int LastMinutes,
    double Change, SeasonJumpDto? Jump);

/// <summary>
/// Episode lengths: how long a show's episodes run season by season, from the provider's runtimes (on 98% of aired
/// episodes). Each regular season's median runtime is one SQL aggregate across the catalog, so a few long specials or a
/// double-length finale don't move it.
/// <list type="bullet">
/// <item><b>Verdict:</b> the last counted season (<see cref="MinEpisodes"/>+ episodes with a runtime) against the first:
/// longer at <see cref="ChangeShare"/> more and <see cref="ChangeMinutes"/> minutes more, shorter the other way, else
/// steady. Stranger Things went from 52 to 70 minutes; The Office from 26 to 30, which is steady.</item>
/// <item><b>Jump:</b> the biggest change between consecutive counted seasons, when it's <see cref="JumpMinutes"/> or
/// more (Game of Thrones' final season: 60 to 79).</item>
/// <item><b>Daily shows are left out</b> (a season of more than 60 episodes), as elsewhere: their runtimes are a format, not a
/// show's choices.</item>
/// </list>
/// Everything is computed together and cached <see cref="CacheFor"/>; a show page reads its row from that.
/// </summary>
public static class EpisodeLengthsApi
{
    public const int MinEpisodes = 3;
    public const double ChangeShare = 0.2;
    public const int ChangeMinutes = 8;
    public const int JumpMinutes = 10;
    private const int ListSize = 25;

    /// <summary>The lists start from episodes at least this long: shorter ones are TVmaze's shorts and compilations.</summary>
    private const int ListMinMinutes = 15;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private sealed record SeasonRow(Guid ShowId, int SeasonNumber, int Episodes, int Timed, double Median, int Total);

    private sealed record ShowRow(Guid Id, string Title, string? PosterUrl, double Popularity, bool Scripted);

    /// <summary>Every show's lengths, cached as one entry (a few hundred shows).</summary>
    private sealed record AllLengths(List<EpisodeLengthsDto> Shows, List<ShowRow> Titles);

    public static void MapEpisodeLengthsApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/episode-lengths", GetShowAsync).RequireFeature(FeatureFlags.EpisodeLengths);
        api.MapGet("/episode-lengths", GetListsAsync).RequireFeature(FeatureFlags.EpisodeLengths);
    }

    private static async Task<Results<Ok<EpisodeLengthsDto>, NotFound>> GetShowAsync(Guid id, CatalogContext db, HybridCache cache,
        TimeProvider time, CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, time, ct);
        return all.Shows.FirstOrDefault(s => s.ShowId == id) is { } show ? TypedResults.Ok(show) : TypedResults.NotFound();
    }

    private static async Task<Ok<EpisodeLengthListsDto>> GetListsAsync(CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, time, ct);
        var titles = all.Titles.ToDictionary(t => t.Id);
        LengthChangeDto Change(EpisodeLengthsDto s)
        {
            var counted = s.Seasons.Where(x => x.Counted).ToList();
            var show = titles[s.ShowId];
            return new LengthChangeDto(s.ShowId, show.Title, show.PosterUrl, counted[0].Number, s.FirstMinutes, counted[^1].Number, s.LastMinutes,
                Math.Round((double)(s.LastMinutes - s.FirstMinutes) / s.FirstMinutes, 2), s.Jump);
        }
        // Scripted shows only, most popular first: by size of change, the lists were led by data quirks (a cartoon of
        // 3-minute shorts "growing" to 59) and competition shows doubling to two-hour episodes.
        var changes = all.Shows
            .Where(s => s.Verdict != "steady" && s.FirstMinutes >= ListMinMinutes && titles.TryGetValue(s.ShowId, out var t) && t.Scripted)
            .Select(Change)
            .OrderByDescending(c => titles[c.ShowId].Popularity).ThenByDescending(c => Math.Abs(c.Change))
            .ToList();
        return TypedResults.Ok(new EpisodeLengthListsDto(
            changes.Where(c => c.Change > 0).Take(ListSize).ToList(),
            changes.Where(c => c.Change < 0).Take(ListSize).ToList()));
    }

    private static async Task<AllLengths> LoadAsync(CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct) =>
        await cache.GetOrCreateAsync("episode-lengths", async token =>
        {
            var now = time.GetUtcNow();
            var rows = await db.Database.SqlQuery<SeasonRow>($"""
                SELECT "ShowId", "SeasonNumber", COUNT(*)::int AS "Episodes", COUNT("RuntimeMinutes")::int AS "Timed",
                       COALESCE(percentile_cont(0.5) WITHIN GROUP (ORDER BY "RuntimeMinutes"), 0) AS "Median",
                       COALESCE(SUM("RuntimeMinutes"), 0)::int AS "Total"
                FROM episodes
                WHERE "SeasonNumber" > 0 AND "AirsAtUtc" <= {now}
                GROUP BY "ShowId", "SeasonNumber"
                """).ToListAsync(token);
            var shows = rows.GroupBy(r => r.ShowId)
                .Where(g => g.All(r => r.Episodes <= ShowRules.DailySeason))
                .Select(g => Build(g.Key, g.OrderBy(r => r.SeasonNumber).ToList()))
                .OfType<EpisodeLengthsDto>()
                .ToList();
            var ids = shows.Select(s => s.ShowId).ToList();
            var titles = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new ShowRow(s.Id, s.Title, s.PosterUrl, s.Popularity, s.Keywords.Contains("scripted")))
                .ToListAsync(token);
            return new AllLengths(shows, titles);
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);

    /// <summary>A show's lengths, or null when fewer than two seasons count.</summary>
    private static EpisodeLengthsDto? Build(Guid showId, List<SeasonRow> seasons)
    {
        var dtos = seasons.Select(r => new SeasonLengthDto(r.SeasonNumber, r.Episodes, (int)Math.Round(r.Median), r.Total, r.Timed >= MinEpisodes))
            .ToList();
        var counted = dtos.Where(d => d.Counted).ToList();
        if (counted.Count < 2)
        {
            return null;
        }

        var (first, last) = (counted[0].Minutes, counted[^1].Minutes);
        var verdict = last >= first * (1 + ChangeShare) && last - first >= ChangeMinutes ? "longer"
            : last <= first * (1 - ChangeShare) && first - last >= ChangeMinutes ? "shorter"
            : "steady";
        var jump = counted.Zip(counted.Skip(1))
            .Select(p => new SeasonJumpDto(p.First.Number, p.Second.Number, p.First.Minutes, p.Second.Minutes))
            .MaxBy(j => Math.Abs(j.ToMinutes - j.FromMinutes));
        return new EpisodeLengthsDto(showId, verdict, first, last,
            jump is not null && Math.Abs(jump.ToMinutes - jump.FromMinutes) >= JumpMinutes ? jump : null, dtos);
    }
}
