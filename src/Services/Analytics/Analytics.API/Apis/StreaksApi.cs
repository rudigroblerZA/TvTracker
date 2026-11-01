using System.Globalization;
using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="BulkLeftOut">Marks of a back catalog left out (<see cref="CopyRules.BulkMark"/>+ at once).</param>
/// <param name="Days">Days in a row with something watched.</param>
/// <param name="Weeks">Weeks (Monday to Sunday) in a row with something watched; the longest run's dates are its first Monday
/// and last Sunday.</param>
/// <param name="KeptUp">Shows whose new episodes the caller watches within <see cref="StreaksApi.KeptUpDays"/> days of airing,
/// longest current run first.</param>
/// <param name="Due">Aired episodes still inside their window that would extend a kept-up run, soonest deadline first.</param>
/// <param name="Best">The longest kept-up run ever, on any show.</param>
public record StreaksDto(int Episodes, int BulkLeftOut, StreakDto Days, StreakDto Weeks, IReadOnlyList<KeptUpDto> KeptUp,
    IReadOnlyList<StreakDueDto> Due, KeptUpDto? Best);

/// <param name="Current">The run that's still alive: ending today (this week), or yesterday (last week) with today
/// (this week) still to come.</param>
/// <param name="AtRisk">Alive, but nothing watched yet today (this week): watch something to keep it.</param>
public record StreakDto(int Current, int Longest, DateOnly? LongestFrom, DateOnly? LongestTo, bool AtRisk);

/// <param name="Current">Episodes in a row, up to the latest aired one, each watched within the window.</param>
/// <param name="Longest">The show's longest such run.</param>
/// <param name="Season">The latest episode in the current run (or the longest, for <see cref="StreaksDto.Best"/>).</param>
public record KeptUpDto(Guid ShowId, string Title, string? PosterUrl, int Current, int Longest, int Season, int Episode);

/// <param name="WatchBy">The last day (in the caller's time zone) that watching it keeps the run.</param>
/// <param name="Streak">The run it would extend.</param>
public record StreakDueDto(Guid ShowId, string Title, string? PosterUrl, int Season, int Episode, DateOnly WatchBy, int Streak);

/// <summary>
/// Streaks: computed on request from the caller's own watches (no other user's data, so no minimum counts), in their
/// profile time zone (<c>tz</c>), as Your TV clock does.
/// <list type="bullet">
/// <item><b>Days and weeks:</b> local dates with a watch, in a row. A run stays alive until the day (week) after its
/// last one ends, so a streak isn't lost at midnight before the evening's episode; <c>AtRisk</c> says so.</item>
/// <item><b>Kept up</b> is TV's own streak: a show's regular episodes in air order, each watched within
/// <see cref="KeptUpDays"/> days of airing (or up to a day before, for a mark made at a premiere screening or across time
/// zones). Episodes from before the first one kept up don't break anything; a whole-season drop counts if binged within
/// the window. An episode still inside its window is neither kept nor missed yet: it's due.</item>
/// <item><b>Bulk marks are left out</b> (<see cref="CopyRules.BulkMark"/> or more of a show at once): they say when a back
/// catalog was entered, not when it was watched.</item>
/// <item><b>No titles:</b> episodes are given by number, as Spoiler watch does; a due episode is by definition unseen.</item>
/// </list>
/// </summary>
public static class StreaksApi
{
    public const int KeptUpDays = 7;
    private const int MinKeptUp = 2;
    private const int MaxShows = 12;
    private static readonly TimeSpan Early = TimeSpan.FromDays(1);

    public static void MapStreaksApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/streaks", GetAsync).RequireFeature(FeatureFlags.Streaks);

    private sealed record Aired(Guid EpisodeId, Guid ShowId, int Season, int Episode, DateTimeOffset AirsAtUtc);

    /// <param name="tz">The caller's time zone (IANA id, from their profile); UTC when missing or unknown.</param>
    private static async Task<Ok<StreaksDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, string? tz = null,
        CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var zone = tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found) ? found : TimeZoneInfo.Utc;
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        var rows = await db.Watches.AsNoTracking().Where(w => w.UserId == userId)
            .Select(w => new { w.EpisodeId, w.ShowId, w.WatchedAtUtc }).ToListAsync(ct);
        var bulk = rows.GroupBy(r => (r.ShowId, r.WatchedAtUtc)).Where(g => g.Count() >= CopyRules.BulkMark).Select(g => g.Key).ToHashSet();
        var watches = rows.Where(r => !bulk.Contains((r.ShowId, r.WatchedAtUtc))).ToList();

        DateOnly Local(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
        var days = watches.Select(w => Local(w.WatchedAtUtc)).Distinct().ToList();
        var dayStreak = Runs(days, d => d.DayNumber, today.DayNumber);
        var weekStreak = Runs(days.Select(WeekStart).Distinct().ToList(), d => d.DayNumber / 7, WeekStart(today).DayNumber / 7);
        // A run of weeks ends on its last week's Sunday, not the Monday that starts it.
        weekStreak = weekStreak with { LongestTo = weekStreak.LongestTo?.AddDays(6) };

        // Kept up: every regular aired episode of the shows the caller watched outside bulk marks, in air order.
        var showIds = watches.Select(w => w.ShowId).Distinct().ToList();
        var aired = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc != null && e.AirsAtUtc <= now)
            .Select(e => new Aired(e.EpisodeId, e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.AirsAtUtc!.Value))
            .ToListAsync(ct);
        var watchedAt = watches.GroupBy(w => w.EpisodeId).ToDictionary(g => g.Key, g => g.Min(w => w.WatchedAtUtc));
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId))
            .Select(s => new { s.ShowId, s.Title, s.PosterUrl }).ToDictionaryAsync(s => s.ShowId, ct);

        var keptUp = new List<KeptUpDto>();
        var due = new List<StreakDueDto>();
        KeptUpDto? best = null;
        foreach (var show in aired.GroupBy(e => e.ShowId).Where(g => shows.ContainsKey(g.Key)))
        {
            var info = shows[show.Key];
            // Kept (true), missed (false) or still due (null), oldest first.
            var states = show.OrderBy(e => e.AirsAtUtc).ThenBy(e => e.Season).ThenBy(e => e.Episode)
                .Select(e =>
                {
                    var deadline = e.AirsAtUtc.AddDays(KeptUpDays);
                    bool? kept = watchedAt.TryGetValue(e.EpisodeId, out var at)
                        && new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) is var w
                        && w >= e.AirsAtUtc - Early && w <= deadline
                            ? true
                            : deadline > now && !watchedAt.ContainsKey(e.EpisodeId) ? null : false;
                    return (Episode: e, Kept: kept, Deadline: deadline);
                })
                .SkipWhile(s => s.Kept != true)
                .ToList();
            if (states.Count == 0)
            {
                continue;
            }

            int longest = 0, run = 0;
            Aired? longestEnd = null;
            foreach (var s in states.Where(s => s.Kept is not null))
            {
                run = s.Kept == true ? run + 1 : 0;
                if (run > longest)
                {
                    (longest, longestEnd) = (run, s.Episode);
                }
            }
            var decided = states.Where(s => s.Kept is not null).ToList();
            var current = decided.AsEnumerable().Reverse().TakeWhile(s => s.Kept == true).Count();
            var latest = decided.LastOrDefault(s => s.Kept == true).Episode;

            if (current >= MinKeptUp && latest is not null)
            {
                keptUp.Add(new KeptUpDto(show.Key, info.Title, info.PosterUrl, current, longest, latest.Season, latest.Episode));
                due.AddRange(states.Where(s => s.Kept is null).Select(s => new StreakDueDto(show.Key, info.Title, info.PosterUrl,
                    s.Episode.Season, s.Episode.Episode, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Deadline, zone).DateTime), current)));
            }
            if (longestEnd is not null && longest >= MinKeptUp && (best is null || longest > best.Longest))
            {
                best = new KeptUpDto(show.Key, info.Title, info.PosterUrl, current, longest, longestEnd.Season, longestEnd.Episode);
            }
        }

        return TypedResults.Ok(new StreaksDto(watches.Count, rows.Count - watches.Count, dayStreak, weekStreak,
            keptUp.OrderByDescending(k => k.Current).ThenBy(k => k.Title, StringComparer.Ordinal).Take(MaxShows).ToList(),
            due.OrderBy(d => d.WatchBy).ThenByDescending(d => d.Streak).ThenBy(d => d.Season).ThenBy(d => d.Episode).Take(MaxShows).ToList(),
            best));
    }

    /// <summary>The Monday a date's week starts on.</summary>
    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// The current and longest runs of consecutive units (days, or weeks by their Monday) in <paramref name="dates"/>; the
    /// current one ends on <paramref name="now"/>'s unit or the one before.
    /// </summary>
    private static StreakDto Runs(List<DateOnly> dates, Func<DateOnly, int> unit, int now)
    {
        var ordered = dates.OrderBy(unit).ToList();
        int longest = 0, run = 0;
        DateOnly? from = null, to = null, runFrom = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            run = i > 0 && unit(ordered[i]) == unit(ordered[i - 1]) + 1 ? run + 1 : 1;
            runFrom = run == 1 ? ordered[i] : runFrom;
            if (run > longest)
            {
                (longest, from, to) = (run, runFrom, ordered[i]);
            }
        }
        var last = ordered.Count > 0 ? unit(ordered[^1]) : int.MinValue;
        var current = last >= now - 1 ? run : 0;
        return new StreakDto(current, longest, from, to, current > 0 && last < now);
    }
}
