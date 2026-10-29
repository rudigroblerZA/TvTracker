using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using System.Security.Claims;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <param name="Date">The week's middle day: today (every past year) or the date asked for (that year only).</param>
/// <param name="EveryYear">True for "this week in TV history", across every past year; false for one date's week.</param>
/// <param name="Premieres">Series premieres that week, best known first.</param>
/// <param name="Finales">Series finales of shows that have ended, best known first.</param>
/// <param name="BestEpisodes">Every year: the best-rated episode of each year, latest year first. One date: that week's best.</param>
/// <param name="OnAir">One date only: shows with an episode within a week either side, best known first.</param>
public record TimeMachineDto(
    DateOnly Date,
    bool EveryYear,
    IReadOnlyList<TimeMachineEpisodeDto> Premieres,
    IReadOnlyList<TimeMachineEpisodeDto> Finales,
    IReadOnlyList<TimeMachineEpisodeDto> BestEpisodes,
    IReadOnlyList<TimeMachineShowDto> OnAir);

/// <param name="Watched">Whether the caller has watched this episode.</param>
public record TimeMachineEpisodeDto(
    Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title,
    DateTimeOffset AiredAtUtc, double? Rating, bool Watched);

/// <param name="SeasonNumber">The season airing then.</param>
public record TimeMachineShowDto(Guid ShowId, string Title, string? PosterUrl, int SeasonNumber);

/// <summary>
/// The TV time machine: what premiered, ended and stood out in a week of TV history, from the catalog copy's air dates
/// and the provider's episode ratings. With no date, it's this week in every past year; with one, that date's week (a
/// birthday, say). Weeks are calendar days in the caller's time zone, so an evening premiere in New York counts on its
/// own day. Each week is a range scan of the air-time index, and a result is cached for everyone in that time zone that
/// day; only the Watched marks are per caller.
/// </summary>
public static class TimeMachineApi
{
    /// <summary>Days either side of the date that count as its week.</summary>
    private const int HalfWeek = 3;

    /// <summary>One date's "on air then": shows with an episode within this many days either side.</summary>
    private const int OnAirDays = 7;

    private const int MaxMilestones = 12;
    private const int MaxBestEpisodes = 12;
    private const int MaxOnAir = 16;

    /// <summary>
    /// An episode rated more than this above its show is left out of the best episodes: thinly rated shows get a few
    /// devoted fans' 10s (an episode at 10 for a show at 7.4).
    /// </summary>
    private const double MaxAboveShow = 1.5;

    private static readonly DateOnly Earliest = new(1950, 1, 1);

    private static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) };

    public static void MapTimeMachineApi(this RouteGroupBuilder api) =>
        api.MapGet("/insights/time-machine", GetAsync).RequireFeature(FeatureFlags.TimeMachine);

    /// <param name="date">A past date (yyyy-MM-dd) to travel to; leave out for this week in every past year.</param>
    /// <param name="tz">The caller's time zone (IANA id, from their profile); UTC when missing or unknown.</param>
    private static async Task<Results<Ok<TimeMachineDto>, BadRequest<string>>> GetAsync(
        ClaimsPrincipal user, AnalyticsContext db, HybridCache cache, TimeProvider time, DateOnly? date = null, string? tz = null,
        CancellationToken ct = default)
    {
        var zone = tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found) ? found : TimeZoneInfo.Utc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
        if (date is { } asked && (asked < Earliest || asked >= today))
        {
            return TypedResults.BadRequest($"Pick a date from {Earliest:yyyy} up to yesterday.");
        }

        var machine = await cache.GetOrCreateAsync(
            $"analytics:time-machine:{date?.ToString("yyyy-MM-dd") ?? $"every-year:{today:yyyy-MM-dd}"}:{zone.Id}",
            token => new ValueTask<TimeMachineDto>(ComputeAsync(db, zone, today, date, token)),
            CacheFor, cancellationToken: ct);

        var ids = machine.Premieres.Concat(machine.Finales).Concat(machine.BestEpisodes).Select(e => e.EpisodeId).Distinct().ToList();
        var watched = user.FindFirst("sub")?.Value is { } userId
            ? (await db.Watches.AsNoTracking().Where(w => w.UserId == userId && ids.Contains(w.EpisodeId)).Select(w => w.EpisodeId).ToListAsync(ct)).ToHashSet()
            : [];
        List<TimeMachineEpisodeDto> Mark(IEnumerable<TimeMachineEpisodeDto> list) => list.Select(e => e with { Watched = watched.Contains(e.EpisodeId) }).ToList();

        return TypedResults.Ok(machine with { Premieres = Mark(machine.Premieres), Finales = Mark(machine.Finales), BestEpisodes = Mark(machine.BestEpisodes) });
    }

    private static async Task<TimeMachineDto> ComputeAsync(AnalyticsContext db, TimeZoneInfo zone, DateOnly today, DateOnly? date, CancellationToken ct)
    {
        // Every year: the same day in each past year, its week taken whole. Near New Year that week spans two years
        // (29 December to 4 January), and the latest one is still a year back.
        var everyYear = date is null;
        var middles = everyYear
            ? Enumerable.Range(Earliest.Year, today.Year - Earliest.Year).Select(year => SameDay(today, year)).ToList()
            : [date!.Value];
        var episodes = await InWeeksAsync(db, zone, middles, HalfWeek, ct);

        var showIds = episodes.Select(e => e.ShowId).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        // Each show's first and last regular episode, to tell premieres and finales.
        var spans = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc != null)
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, First = g.Min(e => e.AirsAtUtc), Last = g.Max(e => e.AirsAtUtc) })
            .ToDictionaryAsync(s => s.ShowId, ct);

        // By episode number, so when several episodes aired at once (a streaming drop) the premiere is the first of them
        // and the finale the last; which episodes those are comes from the air-time spans.
        var known = episodes.Where(e => shows.ContainsKey(e.ShowId)).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
        TimeMachineEpisodeDto Dto(EpisodeCopy e)
        {
            var show = shows[e.ShowId];
            return new TimeMachineEpisodeDto(show.ShowId, show.Title, show.PosterUrl, e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.Title,
                e.AirsAtUtc!.Value, e.ExternalRating, false);
        }
        IEnumerable<EpisodeCopy> BestKnownFirst(IEnumerable<EpisodeCopy> list) => list
            .OrderByDescending(e => shows[e.ShowId].Popularity)
            .ThenByDescending(e => shows[e.ShowId].ExternalRating ?? 0);

        var premieres = BestKnownFirst(known.Where(e => spans[e.ShowId].First == e.AirsAtUtc).DistinctBy(e => e.ShowId))
            .Take(MaxMilestones).Select(Dto).ToList();

        // A finale only once the show has ended, not one between seasons.
        var finales = BestKnownFirst(known.Where(e => shows[e.ShowId].Status == ShowStatus.Ended && spans[e.ShowId].Last == e.AirsAtUtc).Reverse().DistinctBy(e => e.ShowId))
            .Take(MaxMilestones).Select(Dto).ToList();

        var rated = known
            .Where(e => e.ExternalRating is { } r && shows[e.ShowId].ExternalRating is { } s && r - s <= MaxAboveShow)
            .OrderByDescending(e => e.ExternalRating)
            .ThenByDescending(e => shows[e.ShowId].Popularity)
            .ToList();

        // Every year: one per year's week (by the week's middle day, as a week can span New Year), latest first.
        var best = everyYear
            ? rated.GroupBy(e => WeekOf(e, zone, middles)).OrderByDescending(g => g.Key).Select(g => g.First()).Take(MaxBestEpisodes)
            : rated.DistinctBy(e => e.ShowId).Take(MaxBestEpisodes);

        var onAir = new List<TimeMachineShowDto>();
        if (!everyYear)
        {
            var airing = (await InWeeksAsync(db, zone, middles, OnAirDays, ct))
                .GroupBy(e => e.ShowId)
                .Select(g => (ShowId: g.Key, Season: g.Max(e => e.SeasonNumber)))
                .ToList();
            var airingIds = airing.Select(a => a.ShowId).ToList();
            var airingShows = await db.Shows.AsNoTracking().Where(s => airingIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
            onAir = airing
                .Where(a => airingShows.ContainsKey(a.ShowId))
                .Select(a => (Show: airingShows[a.ShowId], a.Season))
                .OrderByDescending(a => a.Show.Popularity)
                .ThenByDescending(a => a.Show.ExternalRating ?? 0)
                .Take(MaxOnAir)
                .Select(a => new TimeMachineShowDto(a.Show.ShowId, a.Show.Title, a.Show.PosterUrl, a.Season))
                .ToList();
        }

        return new TimeMachineDto(date ?? today, everyYear, premieres, finales, best.Select(Dto).ToList(), onAir);
    }

    /// <summary>
    /// Regular episodes that aired within <paramref name="halfWidth"/> days of any of <paramref name="middles"/>, in
    /// <paramref name="zone"/>'s calendar: one range scan of the air-time index per day.
    /// </summary>
    private static Task<List<EpisodeCopy>> InWeeksAsync(AnalyticsContext db, TimeZoneInfo zone, List<DateOnly> middles, int halfWidth, CancellationToken ct)
    {
        var starts = middles.Select(m => StartOfDayUtc(m.AddDays(-halfWidth), zone)).ToArray();
        var ends = middles.Select(m => StartOfDayUtc(m.AddDays(halfWidth + 1), zone)).ToArray();
        return db.Episodes
            .FromSqlRaw(
                "SELECT e.* FROM episodes e JOIN unnest({0}, {1}) AS w(starts, ends) " +
                "ON e.\"AirsAtUtc\" >= w.starts AND e.\"AirsAtUtc\" < w.ends WHERE e.\"SeasonNumber\" > 0", starts, ends)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>The same month and day in <paramref name="year"/>; 29 February becomes the 28th in other years.</summary>
    private static DateOnly SameDay(DateOnly day, int year) => new(year, day.Month, Math.Min(day.Day, DateTime.DaysInMonth(year, day.Month)));

    /// <summary>Midnight starting <paramref name="day"/> in <paramref name="zone"/>, as UTC (or the first valid time after it).</summary>
    private static DateTime StartOfDayUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>The middle day of the week an episode fell in.</summary>
    private static DateOnly WeekOf(EpisodeCopy episode, TimeZoneInfo zone, List<DateOnly> middles)
    {
        var aired = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(episode.AirsAtUtc!.Value, zone).DateTime);
        return middles.MinBy(m => Math.Abs(m.DayNumber - aired.DayNumber));
    }
}
