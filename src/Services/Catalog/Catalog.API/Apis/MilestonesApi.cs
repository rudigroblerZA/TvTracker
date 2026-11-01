using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Upcoming">Milestone episodes airing in the next <see cref="MilestonesApi.AheadDays"/> days, soonest first.</param>
/// <param name="Reached">Milestone episodes that aired in the last <see cref="MilestonesApi.BackDays"/> days, latest first.</param>
public record MilestonesDto(IReadOnlyList<MilestoneDto> Upcoming, IReadOnlyList<MilestoneDto> Reached);

/// <param name="Number">The episode's place among the show's regular episodes: 100 for the 100th.</param>
/// <param name="Story">A scripted or animated show (the default list); the rest come with <c>all=true</c>.</param>
public record MilestoneDto(Guid ShowId, string Title, string? PosterUrl, int Number, int Season, int Episode, DateTimeOffset AirsAtUtc, bool Story);

/// <summary>
/// Episode milestones: a show's 100th, 150th, 200th, 250th, 300th, 400th, 500th, 600th, 700th, 750th, 800th, 900th or
/// 1,000th episode (<see cref="Milestones"/>), coming up or just aired, counted from the catalog's regular episodes in
/// season and episode order, a number listed twice (a same-night crossover) counted once.
/// <list type="bullet">
/// <item><b>The catalog's count:</b> specials (season 0) are left out, and a network can count its own way (a double
/// episode as one or two, a special as an episode), so a celebrated "100th" can land one either side; the page says so.</item>
/// <item><b>No titles:</b> a milestone episode is often an event, and its title can give it away.</item>
/// <item><b>Story shows by default:</b> daily shows (a season over <see cref="ShowRules.DailySeason"/> episodes) are
/// left out, and every other kind (talk, news, reality, sports, game shows, …) and shows with no kind only come with
/// <c>all=true</c>; otherwise donghua with 200-episode seasons and daily magazines crowd out Chicago Fire's 300th.</item>
/// </list>
/// One window function over the episodes, cached <see cref="CacheFor"/>.
/// </summary>
public static class MilestonesApi
{
    public const int AheadDays = 120;
    public const int BackDays = 30;
    private const int MaxRows = 40;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    /// <summary>The episode numbers that count as milestones.</summary>
    public static readonly int[] Milestones = [100, 150, 200, 250, 300, 400, 500, 600, 700, 750, 800, 900, 1000];

    private sealed record Row(Guid ShowId, int Number, int SeasonNumber, int EpisodeNumber, DateTimeOffset AirsAtUtc);

    private sealed record ShowRow(Guid Id, string Title, string? PosterUrl, bool Story);

    public static void MapMilestonesApi(this RouteGroupBuilder api) =>
        api.MapGet("/milestones", GetAsync).RequireFeature(FeatureFlags.Milestones);

    private static async Task<Ok<MilestonesDto>> GetAsync(bool? all, CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct)
    {
        var every = await LoadAsync(db, cache, time, ct);
        var now = time.GetUtcNow();
        var shown = every.Where(m => all == true || m.Story).ToList();
        return TypedResults.Ok(new MilestonesDto(
            shown.Where(m => m.AirsAtUtc > now).OrderBy(m => m.AirsAtUtc).Take(MaxRows).ToList(),
            shown.Where(m => m.AirsAtUtc <= now).OrderByDescending(m => m.AirsAtUtc).Take(MaxRows).ToList()));
    }

    private static async Task<List<MilestoneDto>> LoadAsync(CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct) =>
        await cache.GetOrCreateAsync("milestones", async token =>
        {
            var now = time.GetUtcNow();
            var from = now.AddDays(-BackDays);
            var to = now.AddDays(AheadDays);
            var milestones = Milestones;
            // Numbered over every regular episode, dated or not, so an undated episode still takes its place in the count.
            // TVmaze lists a same-night crossover's number twice; one row per number (the earliest aired) keeps the count
            // and the numbering fixed. Season sizes come from the same pass, for the daily-show test.
            var rows = await db.Database.SqlQuery<Row>($"""
                WITH distinct_episodes AS (
                    SELECT DISTINCT ON ("ShowId", "SeasonNumber", "EpisodeNumber") "ShowId", "SeasonNumber", "EpisodeNumber", "AirsAtUtc"
                    FROM episodes
                    WHERE "SeasonNumber" > 0
                    ORDER BY "ShowId", "SeasonNumber", "EpisodeNumber", "AirsAtUtc" NULLS LAST, "Id"
                ),
                sized AS (
                    SELECT *, count(*) OVER (PARTITION BY "ShowId", "SeasonNumber") AS "SeasonSize"
                    FROM distinct_episodes
                ),
                numbered AS (
                    SELECT *,
                           row_number() OVER (PARTITION BY "ShowId" ORDER BY "SeasonNumber", "EpisodeNumber")::int AS "Number",
                           max("SeasonSize") OVER (PARTITION BY "ShowId") AS "LargestSeason"
                    FROM sized
                )
                SELECT "ShowId", "Number", "SeasonNumber", "EpisodeNumber", "AirsAtUtc"
                FROM numbered
                WHERE "Number" = ANY({milestones}) AND "AirsAtUtc" BETWEEN {from} AND {to} AND "LargestSeason" <= {ShowRules.DailySeason}
                """).ToListAsync(token);
            var ids = rows.Select(r => r.ShowId).Distinct().ToList();
            var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new ShowRow(s.Id, s.Title, s.PosterUrl, s.Keywords.Any(k => ShowRules.StoryKinds.Contains(k))))
                .ToDictionaryAsync(s => s.Id, token);
            return rows.Where(r => shows.ContainsKey(r.ShowId))
                .Select(r => new MilestoneDto(r.ShowId, shows[r.ShowId].Title, shows[r.ShowId].PosterUrl, r.Number, r.SeasonNumber, r.EpisodeNumber,
                    r.AirsAtUtc, shows[r.ShowId].Story))
                .ToList();
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);
}
