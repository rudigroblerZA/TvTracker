using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="Gaps">Each wait from a season's last episode to the next one's first, in order.</param>
/// <param name="TypicalDays">The median wait, given from three waits up.</param>
/// <param name="Longest">The longest wait.</param>
/// <param name="Now">For a running show: how long since its last episode, and when it's back if that's dated. Null for an
/// ended show.</param>
public record SeasonGapsDto(Guid ShowId, IReadOnlyList<SeasonGapDto> Gaps, int? TypicalDays, SeasonGapDto? Longest, WaitingNowDto? Now);

/// <param name="Scheduled">The next season's first episode hasn't aired yet; the wait runs to its date.</param>
public record SeasonGapDto(int FromSeason, int ToSeason, DateOnly LastEpisode, DateOnly NextEpisode, int Days, bool Scheduled);

/// <param name="Since">The last aired episode.</param>
/// <param name="BackOn">The next dated episode, if any.</param>
public record WaitingNowDto(DateOnly Since, int Days, DateOnly? BackOn);

/// <param name="Longest">The longest single waits under <see cref="SeasonGapsApi.RevivalDays"/>, popular scripted shows.</param>
/// <param name="Revived">Shows that came back after <see cref="SeasonGapsApi.RevivalDays"/> or more.</param>
/// <param name="WaitingNow">Running scripted shows with nothing aired for a year or more, longest first.</param>
public record SeasonGapListsDto(IReadOnlyList<GapShowDto> Longest, IReadOnlyList<GapShowDto> Revived, IReadOnlyList<GapShowDto> WaitingNow);

/// <param name="Gap">The wait it's listed for; null in <see cref="SeasonGapListsDto.WaitingNow"/>, which has <paramref name="Now"/>.</param>
public record GapShowDto(Guid ShowId, string Title, string? PosterUrl, SeasonGapDto? Gap, WaitingNowDto? Now);

/// <summary>
/// The waits between seasons, from the catalog's air dates: one SQL aggregate gives each regular season's first and last
/// dated episode, and a wait runs from a season's last episode to the next season's first (Stranger Things' season 5 came
/// 1,244 days after season 4 ended). A next season with a dated first episode still to air counts as a scheduled wait,
/// up to that date.
/// <list type="bullet">
/// <item><b>Revivals</b> are waits of <see cref="RevivalDays"/> (five years) or more: Twin Peaks' 26 years. They have a
/// list of their own, since in the longest-waits list they'd crowd out every ordinary hiatus.</item>
/// <item><b>Waiting now:</b> a running show whose last aired episode is a year or more ago, with its next date if
/// TVmaze has one. The catalog already calls a show ended after four quiet years with nothing scheduled.</item>
/// <item><b>Lists:</b> scripted shows of <see cref="ListMinPopularity"/>+ popularity, so the lists name shows people know;
/// daily shows are left out, as elsewhere.</item>
/// </list>
/// Computed together and cached <see cref="CacheFor"/>; a show page reads its row from that.
/// </summary>
public static class SeasonGapsApi
{
    public const int RevivalDays = 5 * 365;
    public const int WaitingDays = 365;
    public const double ListMinPopularity = 90;
    private const int ListSize = 25;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private sealed record SeasonRow(Guid ShowId, int SeasonNumber, int Episodes, DateTimeOffset First, DateTimeOffset Last,
        DateTimeOffset? LastAired, DateTimeOffset? NextUp);

    private sealed record ShowRow(Guid Id, string Title, string? PosterUrl, double Popularity, bool Scripted, bool Running);

    private sealed record AllGaps(List<SeasonGapsDto> Shows, List<ShowRow> Titles);

    public static void MapSeasonGapsApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/season-gaps", GetShowAsync).RequireFeature(FeatureFlags.SeasonGaps);
        api.MapGet("/season-gaps", GetListsAsync).RequireFeature(FeatureFlags.SeasonGaps);
    }

    private static async Task<Results<Ok<SeasonGapsDto>, NotFound>> GetShowAsync(Guid id, CatalogContext db, HybridCache cache, TimeProvider time,
        CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, time, ct);
        return all.Shows.FirstOrDefault(s => s.ShowId == id) is { } show ? TypedResults.Ok(show) : TypedResults.NotFound();
    }

    private static async Task<Ok<SeasonGapListsDto>> GetListsAsync(CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, time, ct);
        var titles = all.Titles.Where(t => t.Scripted && t.Popularity >= ListMinPopularity).ToDictionary(t => t.Id);
        var listed = all.Shows.Where(s => titles.ContainsKey(s.ShowId)).ToList();
        GapShowDto Row(SeasonGapsDto s, SeasonGapDto? gap) =>
            new(s.ShowId, titles[s.ShowId].Title, titles[s.ShowId].PosterUrl, gap, gap is null ? s.Now : null);

        return TypedResults.Ok(new SeasonGapListsDto(
            listed.Select(s => (Show: s, Gap: s.Gaps.Where(g => g.Days < RevivalDays).MaxBy(g => g.Days)))
                .Where(x => x.Gap is not null)
                .OrderByDescending(x => x.Gap!.Days).Take(ListSize).Select(x => Row(x.Show, x.Gap)).ToList(),
            listed.Select(s => (Show: s, Gap: s.Gaps.Where(g => g.Days >= RevivalDays).MaxBy(g => g.Days)))
                .Where(x => x.Gap is not null)
                .OrderByDescending(x => x.Gap!.Days).Take(ListSize).Select(x => Row(x.Show, x.Gap)).ToList(),
            listed.Where(s => s.Now is { Days: >= WaitingDays } && titles[s.ShowId].Running)
                .OrderByDescending(s => s.Now!.Days).Take(ListSize).Select(s => Row(s, null)).ToList()));
    }

    private static async Task<AllGaps> LoadAsync(CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct) =>
        await cache.GetOrCreateAsync("season-gaps", async token =>
        {
            var now = time.GetUtcNow();
            var rows = await db.Database.SqlQuery<SeasonRow>($"""
                SELECT "ShowId", "SeasonNumber", COUNT(*)::int AS "Episodes",
                       MIN("AirsAtUtc") AS "First", MAX("AirsAtUtc") AS "Last",
                       MAX("AirsAtUtc") FILTER (WHERE "AirsAtUtc" <= {now}) AS "LastAired",
                       MIN("AirsAtUtc") FILTER (WHERE "AirsAtUtc" > {now}) AS "NextUp"
                FROM episodes
                WHERE "SeasonNumber" > 0 AND "AirsAtUtc" IS NOT NULL
                GROUP BY "ShowId", "SeasonNumber"
                """).ToListAsync(token);
            var shows = rows.GroupBy(r => r.ShowId)
                .Where(g => g.All(r => r.Episodes <= ShowRules.DailySeason))
                .Select(g => Build(g.Key, g.OrderBy(r => r.SeasonNumber).ToList(), now))
                .ToList();
            var ids = shows.Select(s => s.ShowId).ToList();
            var titles = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new ShowRow(s.Id, s.Title, s.PosterUrl, s.Popularity, s.Keywords.Contains("scripted"),
                    s.Status != ShowStatus.Ended && s.Status != ShowStatus.Cancelled))
                .ToListAsync(token);
            // "Waiting now" is about running shows; an ended show's last episode was its last.
            var running = titles.Where(t => t.Running).Select(t => t.Id).ToHashSet();
            return new AllGaps(shows.Select(s => running.Contains(s.ShowId) ? s : s with { Now = null }).ToList(), titles);
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);

    private static SeasonGapsDto Build(Guid showId, List<SeasonRow> seasons, DateTimeOffset now)
    {
        static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);
        var gaps = seasons.Zip(seasons.Skip(1))
            .Where(p => p.Second.First > p.First.Last)
            .Select(p => new SeasonGapDto(p.First.SeasonNumber, p.Second.SeasonNumber, Day(p.First.Last), Day(p.Second.First),
                (int)(p.Second.First - p.First.Last).TotalDays, p.Second.First > now))
            .ToList();
        // The median, from three waits up: two waits make no pattern (Twin Peaks' 130 days and 26 years).
        var days = gaps.Select(g => g.Days).Order().ToList();
        int? typical = days.Count >= 3
            ? days.Count % 2 == 1 ? days[days.Count / 2] : (days[days.Count / 2 - 1] + days[days.Count / 2]) / 2
            : null;

        var lastAired = seasons.Max(s => s.LastAired);
        var nextUp = seasons.Min(s => s.NextUp);
        var waiting = lastAired is { } last
            ? new WaitingNowDto(Day(last), (int)(now - last).TotalDays, nextUp is { } next ? Day(next) : null)
            : null;
        return new SeasonGapsDto(showId, gaps, typical, gaps.MaxBy(g => g.Days), waiting);
    }
}
