using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// The TV time machine (see <see cref="TimeMachine"/>). Each week is a range scan of the air-time index, and a result is
/// cached for everyone in that time zone that day; only the Watched marks are per caller.
/// </summary>
public static class TimeMachineApi
{
    /// <summary>
    /// The key carries the day and time zone, so a day's answer can stay the day.
    /// </summary>
    private static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromHours(24), LocalCacheExpiration = TimeSpan.FromHours(24) };

    public static void MapTimeMachineApi(this RouteGroupBuilder api) =>
        api.MapGet("/insights/time-machine", GetAsync).RequireFeature(FeatureFlags.TimeMachine);

    /// <param name="date">A past date (yyyy-MM-dd) to travel to; leave out for this week in every past year.</param>
    /// <param name="tz">The caller's time zone (IANA id, from their profile); UTC when missing or unknown.</param>
    private static async Task<Results<Ok<TimeMachineDto>, BadRequest<string>>> GetAsync(
        ClaimsPrincipal user, AnalyticsContext db, HybridCache cache, TimeProvider time, DateOnly? date = null, string? tz = null,
        CancellationToken ct = default)
    {
        var zone = Viewer.Zone(tz);
        var today = ViewerRules.LocalDate(time.GetUtcNow(), zone);
        if (date is { } asked && (asked < TimeMachine.Earliest || asked >= today))
        {
            return TypedResults.BadRequest($"Pick a date from {TimeMachine.Earliest:yyyy} up to yesterday.");
        }

        var machine = await cache.GetOrCreateAsync(
            $"analytics:time-machine:{date?.ToString("yyyy-MM-dd") ?? $"every-year:{today:yyyy-MM-dd}"}:{zone.Id}",
            token => new ValueTask<TimeMachineDto>(ComputeAsync(db, zone, today, date, token)),
            CacheFor, cancellationToken: ct);

        var ids = machine.Premieres.Concat(machine.Finales).Concat(machine.BestEpisodes).Select(e => e.EpisodeId).Distinct().ToList();
        var userId = user.GetUserId();
        var watched = (await db.Watches.AsNoTracking().Where(w => w.UserId == userId && ids.Contains(w.EpisodeId)).Select(w => w.EpisodeId).ToListAsync(ct)).ToHashSet();
        List<TimeMachineEpisodeDto> Mark(IEnumerable<TimeMachineEpisodeDto> list) => list.Select(e => e with { Watched = watched.Contains(e.EpisodeId) }).ToList();

        return TypedResults.Ok(machine with { Premieres = Mark(machine.Premieres), Finales = Mark(machine.Finales), BestEpisodes = Mark(machine.BestEpisodes) });
    }

    internal static async Task<TimeMachineDto> ComputeAsync(AnalyticsContext db, TimeZoneInfo zone, DateOnly today, DateOnly? date, CancellationToken ct)
    {
        var middles = TimeMachine.Middles(today, date);
        var episodes = await InWeeksAsync(db, zone, middles, TimeMachine.HalfWeek, ct);

        var showIds = episodes.Select(e => e.ShowId).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        // Each show's first and last regular episode, to tell premieres and finales.
        var spans = await db.Episodes.AsNoTracking()
                .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc != null)
                .GroupBy(e => e.ShowId)
                .Select(g => new { ShowId = g.Key, First = g.Min(e => e.AirsAtUtc), Last = g.Max(e => e.AirsAtUtc) })
                .ToDictionaryAsync(s => s.ShowId, s => (s.First, s.Last), ct);

        if (date is null)
        {
            return TimeMachine.Of(date, today, zone, middles, episodes, shows, spans);
        }

        var airing = await InWeeksAsync(db, zone, middles, TimeMachine.OnAirDays, ct);
        var airingIds = airing.Select(e => e.ShowId).Distinct().ToList();
        var airingShows = await db.Shows.AsNoTracking().Where(s => airingIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        return TimeMachine.Of(date, today, zone, middles, episodes, shows, spans, (airing, airingShows));
    }

    /// <summary>
    /// Regular episodes that aired within <paramref name="halfWidth"/> days of any of <paramref name="middles"/>, in
    /// <paramref name="zone"/>'s calendar: one range scan of the air-time index per day.
    /// </summary>
    internal static Task<List<EpisodeCopy>> InWeeksAsync(AnalyticsContext db, TimeZoneInfo zone, List<DateOnly> middles, int halfWidth, CancellationToken ct)
    {
        var starts = middles.Select(m => TimeMachine.StartOfDayUtc(m.AddDays(-halfWidth), zone)).ToArray();
        var ends = middles.Select(m => TimeMachine.StartOfDayUtc(m.AddDays(halfWidth + 1), zone)).ToArray();
        return db.Episodes
            .FromSqlRaw(
                "SELECT e.* FROM episodes e JOIN unnest({0}, {1}) AS w(starts, ends) " +
                "ON e.\"AirsAtUtc\" >= w.starts AND e.\"AirsAtUtc\" < w.ends WHERE e.\"SeasonNumber\" > 0", starts, ends)
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
