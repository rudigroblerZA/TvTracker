using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <param name="Year">The year shown.</param>
/// <param name="Years">Years with viewing, latest first, for switching.</param>
/// <param name="Bulk">Episodes left out because they were marked many at once (see <see cref="YearInTvApi.BulkMark"/>).</param>
/// <param name="DaysWatched">Days with at least one episode.</param>
/// <param name="LongestStreak">Most days in a row with an episode.</param>
/// <param name="Finished">Ended shows whose last episode they watched this year.</param>
/// <param name="CaughtUp">Running shows they caught up on this year (and still are).</param>
/// <param name="LeftBehind">Shows they watched this year, have episodes left of, and haven't touched for
/// <see cref="InsightRules.StoppedAfterDays"/> days by the year's end (or today).</param>
/// <param name="Shift">How the DNA of what they watched moved since the year before; empty without a year before.</param>
public record YearInTvDto(int Year, IReadOnlyList<int> Years, int Episodes, int Minutes, int Shows, int NewShows, int Bulk,
    int DaysWatched, int LongestStreak, YearDayDto? BusiestDay, YearMonthDto? BusiestMonth, IReadOnlyList<YearShowDto> TopShows,
    IReadOnlyList<YearShareDto> Genres, YearShareDto? TopNetwork, IReadOnlyList<YearShowDto> Finished,
    IReadOnlyList<YearShowDto> CaughtUp, IReadOnlyList<YearShowDto> LeftBehind, YearEpisodeDto? BestEpisode,
    IReadOnlyList<YearShiftDto> Shift, YearCalledItDto? CalledIt);

public record YearShowDto(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes);

public record YearDayDto(DateOnly Date, int Episodes, int Minutes);

public record YearMonthDto(int Month, string Name, int Episodes, int Minutes);

/// <param name="Share">Of the year's minutes, 0-1.</param>
public record YearShareDto(string Name, int Minutes, double Share);

public record YearEpisodeDto(Guid ShowId, string ShowTitle, string? PosterUrl, int SeasonNumber, int EpisodeNumber, string Title, double Rating);

/// <param name="From">This year's trait value, minutes-weighted, 0-1; <paramref name="To"/> the year before's.</param>
/// <param name="Toward">The end it moved towards ("Short episodes").</param>
public record YearShiftDto(string Key, string Name, double From, double To, string Toward);

/// <param name="Ahead">The share of the show's followers here who followed it after them (0-1).</param>
/// <param name="Followers">Its followers here (at least <see cref="InsightRules.MinUsersToShow"/>).</param>
public record YearCalledItDto(Guid ShowId, string Title, string? PosterUrl, DateTime FollowedAtUtc, double Ahead, int Followers, double? Rating);

/// <summary>
/// Year in TV: the caller's year of viewing, computed on request from their own watches, follows and the catalog copy
/// (like "your taste", so no minimum counts), except "called it", which compares their follow with other followers' and
/// so needs <see cref="InsightRules.MinUsersToShow"/> of them.
/// <list type="bullet">
/// <item><b>Days are the user's:</b> watches are placed in the profile time zone (<c>tz</c>), as the time machine does.</item>
/// <item><b>Bulk marks are left out:</b> a mark of <see cref="BulkMark"/> or more of a show's episodes at once ("watched up
/// to S05E10") says when they were marked, not watched, and seeding a back catalog would read as a record year. The
/// count left out is returned, so the page can say so.</item>
/// <item><b>Taste shift:</b> each DNA trait averaged over the year's shows, weighted by minutes watched, against the year
/// before's; traits that moved <see cref="MinShift"/> or more are named, largest first.</item>
/// <item><b>Called it:</b> of the shows they followed that year with a rating of 7.5 or more, the one they followed earliest
/// relative to its other followers here, when they were ahead of at least half of them.</item>
/// </list>
/// </summary>
public static class YearInTvApi
{
    /// <summary>A mark of this many of one show's episodes at the same moment is a bulk mark.</summary>
    public const int BulkMark = 8;

    /// <summary>A trait is named in the shift when the minutes-weighted average moved at least this much.</summary>
    public const double MinShift = 0.1;

    private const int MaxShows = 5;
    private const int MaxListed = 8;
    private const int MaxShifts = 3;
    private const double CalledItMinRating = 7.5;

    public static void MapYearInTvApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/year", GetYearAsync).RequireFeature(FeatureFlags.YearInTv);

    private sealed record Watch(Guid ShowId, Guid EpisodeId, DateTime WatchedAtUtc, DateOnly Day, int Minutes, int Season, int Number,
        string Title, double? Rating);

    /// <param name="year">The year to recap; the latest with viewing when missing.</param>
    /// <param name="tz">The caller's time zone (IANA id, from their profile); UTC when missing or unknown.</param>
    private static async Task<Results<Ok<YearInTvDto>, UnauthorizedHttpResult>> GetYearAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, int? year = null, string? tz = null, CancellationToken ct = default)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var zone = tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found) ? found : TimeZoneInfo.Utc;
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        var rows = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .Join(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, e) => new
            {
                w.ShowId, w.EpisodeId, w.WatchedAtUtc, e.RuntimeMinutes, e.SeasonNumber, e.EpisodeNumber, e.Title, e.ExternalRating
            })
            .ToListAsync(ct);

        var bulk = rows.GroupBy(r => (r.ShowId, r.WatchedAtUtc)).Where(g => g.Count() >= BulkMark).SelectMany(g => g).ToHashSet();
        var watches = rows.Where(r => !bulk.Contains(r))
            .Select(r => new Watch(r.ShowId, r.EpisodeId, r.WatchedAtUtc,
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.WatchedAtUtc, DateTimeKind.Utc), zone)),
                r.RuntimeMinutes ?? 0, r.SeasonNumber, r.EpisodeNumber, r.Title, r.ExternalRating))
            .ToList();
        var years = watches.Select(w => w.Day.Year).Distinct().OrderDescending().ToList();
        var shown = year ?? (years.Count > 0 ? years[0] : today.Year);

        var inYear = watches.Where(w => w.Day.Year == shown).ToList();
        var lastYear = watches.Where(w => w.Day.Year == shown - 1).ToList();
        var bulkInYear = rows.Count(r => bulk.Contains(r)
            && TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.WatchedAtUtc, DateTimeKind.Utc), zone).Year == shown);

        var showIds = inYear.Select(w => w.ShowId).Concat(lastYear.Select(w => w.ShowId)).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        var byShow = inYear.GroupBy(w => w.ShowId).Where(g => shows.ContainsKey(g.Key)).ToList();
        YearShowDto ShowOf(Guid id, IEnumerable<Watch> ws) =>
            new(id, shows[id].Title, shows[id].PosterUrl, ws.Count(), ws.Sum(w => w.Minutes));

        var minutes = inYear.Sum(w => w.Minutes);
        var firstWatch = watches.GroupBy(w => w.ShowId).ToDictionary(g => g.Key, g => g.Min(w => w.Day));
        var days = inYear.GroupBy(w => w.Day).Select(g => new YearDayDto(g.Key, g.Count(), g.Sum(w => w.Minutes))).ToList();

        var (finished, caughtUp, leftBehind) = await ProgressAsync(db, userId, byShow.Select(g => g.Key).ToList(), shows, inYear,
            shown == today.Year ? now.UtcDateTime : new DateTime(shown, 12, 31, 23, 59, 59, DateTimeKind.Utc), now, ct);

        var genres = byShow
            .SelectMany(g => shows[g.Key].Genres.Select(genre => (Genre: genre, Minutes: g.Sum(w => w.Minutes))))
            .GroupBy(x => x.Genre)
            .Select(x => new YearShareDto(x.Key, x.Sum(y => y.Minutes), minutes > 0 ? Math.Round((double)x.Sum(y => y.Minutes) / minutes, 2) : 0))
            .OrderByDescending(x => x.Minutes)
            .Take(MaxShows)
            .ToList();
        var network = byShow.Where(g => shows[g.Key].Network is not null)
            .GroupBy(g => shows[g.Key].Network!)
            .Select(x => new YearShareDto(x.Key, x.Sum(g => g.Sum(w => w.Minutes)),
                minutes > 0 ? Math.Round((double)x.Sum(g => g.Sum(w => w.Minutes)) / minutes, 2) : 0))
            .OrderByDescending(x => x.Minutes)
            .FirstOrDefault();

        var best = inYear.Where(w => w.Rating is not null && w.Season > 0 && shows.ContainsKey(w.ShowId))
            .OrderByDescending(w => w.Rating).ThenBy(w => w.WatchedAtUtc)
            .FirstOrDefault();

        return TypedResults.Ok(new YearInTvDto(
            shown,
            years,
            inYear.Count,
            minutes,
            byShow.Count,
            byShow.Count(g => firstWatch[g.Key].Year == shown),
            bulkInYear,
            days.Count,
            LongestStreak(days.Select(d => d.Date)),
            days.OrderByDescending(d => d.Minutes).ThenByDescending(d => d.Episodes).FirstOrDefault(),
            inYear.GroupBy(w => w.Day.Month)
                .Select(g => new YearMonthDto(g.Key, CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(g.Key), g.Count(), g.Sum(w => w.Minutes)))
                .OrderByDescending(m => m.Minutes).ThenByDescending(m => m.Episodes)
                .FirstOrDefault(),
            byShow.OrderByDescending(g => g.Sum(w => w.Minutes)).ThenByDescending(g => g.Count()).Take(MaxShows)
                .Select(g => ShowOf(g.Key, g)).ToList(),
            genres,
            network,
            finished.Select(id => ShowOf(id, byShow.First(g => g.Key == id))).Take(MaxListed).ToList(),
            caughtUp.Select(id => ShowOf(id, byShow.First(g => g.Key == id))).Take(MaxListed).ToList(),
            leftBehind.Select(id => ShowOf(id, byShow.First(g => g.Key == id))).Take(MaxListed).ToList(),
            best is null ? null : new YearEpisodeDto(best.ShowId, shows[best.ShowId].Title, shows[best.ShowId].PosterUrl, best.Season,
                best.Number, best.Title, best.Rating!.Value),
            await ShiftAsync(db, inYear, lastYear, ct),
            await CalledItAsync(db, userId, shown, zone, ct)));
    }

    /// <summary>Most consecutive days in the set.</summary>
    internal static int LongestStreak(IEnumerable<DateOnly> days)
    {
        var (longest, run) = (0, 0);
        DateOnly? previous = null;
        foreach (var day in days.Distinct().Order())
        {
            run = previous is { } p && p.AddDays(1) == day ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = day;
        }
        return longest;
    }

    /// <summary>
    /// Finished (ended shows, last regular episode watched this year), caught up (running shows, every aired regular
    /// episode watched, the last of them this year) and left behind (episodes left, nothing watched for
    /// <see cref="InsightRules.StoppedAfterDays"/> days by <paramref name="asOf"/>); each most watched first.
    /// </summary>
    private static async Task<(List<Guid> Finished, List<Guid> CaughtUp, List<Guid> LeftBehind)> ProgressAsync(
        AnalyticsContext db, string userId, List<Guid> ids, Dictionary<Guid, ShowCopy> shows, List<Watch> inYear, DateTime asOf,
        DateTimeOffset now, CancellationToken ct)
    {
        var aired = (await db.Episodes.AsNoTracking()
                .Where(e => ids.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
                .Select(e => new { e.ShowId, e.EpisodeId, e.AirsAtUtc })
                .ToListAsync(ct))
            .ToLookup(e => e.ShowId);
        var watched = (await db.Watches.AsNoTracking()
                .Where(w => w.UserId == userId && ids.Contains(w.ShowId))
                .Select(w => new { w.ShowId, w.EpisodeId, w.WatchedAtUtc })
                .ToListAsync(ct))
            .ToLookup(w => w.ShowId);
        var minutes = inYear.GroupBy(w => w.ShowId).ToDictionary(g => g.Key, g => g.Sum(w => w.Minutes));
        var yearEpisodes = inYear.Select(w => w.EpisodeId).ToHashSet();

        List<Guid> finished = [], caughtUp = [], leftBehind = [];
        foreach (var id in ids.OrderByDescending(id => minutes.GetValueOrDefault(id)))
        {
            var episodes = aired[id].OrderBy(e => e.AirsAtUtc).ToList();
            if (episodes.Count == 0)
            {
                continue;
            }
            var seen = watched[id].Select(w => w.EpisodeId).ToHashSet();
            var all = episodes.All(e => seen.Contains(e.EpisodeId));
            var ended = shows[id].Status is ShowStatus.Ended or ShowStatus.Cancelled;
            if (all && yearEpisodes.Contains(episodes[^1].EpisodeId))
            {
                (ended ? finished : caughtUp).Add(id);
            }
            else if (!all && watched[id].Max(w => w.WatchedAtUtc) < asOf.AddDays(-InsightRules.StoppedAfterDays))
            {
                leftBehind.Add(id);
            }
        }
        return (finished, caughtUp, leftBehind);
    }

    /// <summary>The DNA traits that moved most between the year before and this one (minutes-weighted).</summary>
    private static async Task<List<YearShiftDto>> ShiftAsync(AnalyticsContext db, List<Watch> inYear, List<Watch> lastYear, CancellationToken ct)
    {
        if (inYear.Count == 0 || lastYear.Count == 0)
        {
            return [];
        }
        var ids = inYear.Concat(lastYear).Select(w => w.ShowId).Distinct().ToList();
        var documents = await db.ShowInsights.AsNoTracking().Where(i => ids.Contains(i.ShowId))
            .Select(i => new { i.ShowId, i.Document }).ToListAsync(ct);
        var dna = documents.ToDictionary(d => d.ShowId, d => TraitsOf(d.Document));

        Dictionary<string, double> Average(List<Watch> watches)
        {
            var weighted = watches.GroupBy(w => w.ShowId)
                .Where(g => dna.TryGetValue(g.Key, out var t) && t.Count > 0)
                .Select(g => (Traits: dna[g.Key], Minutes: (double)Math.Max(1, g.Sum(w => w.Minutes))))
                .ToList();
            return weighted.SelectMany(w => w.Traits.Select(t => (t.Key, t.Value, w.Minutes)))
                .GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Value * x.Minutes) / g.Sum(x => x.Minutes));
        }

        var now = Average(inYear);
        var before = Average(lastYear);
        return now.Keys.Where(before.ContainsKey).Where(DnaTraits.ByKey.ContainsKey)
            .Select(k => (Key: k, From: now[k], To: before[k]))
            .Where(x => Math.Abs(x.From - x.To) >= MinShift)
            .OrderByDescending(x => Math.Abs(x.From - x.To))
            .Take(MaxShifts)
            .Select(x => new YearShiftDto(x.Key, DnaTraits.ByKey[x.Key].Name, Math.Round(x.From, 2), Math.Round(x.To, 2),
                x.From < x.To ? DnaTraits.ByKey[x.Key].Low : DnaTraits.ByKey[x.Key].High))
            .ToList();
    }

    private static Dictionary<string, double> TraitsOf(string document)
    {
        using var json = JsonDocument.Parse(document);
        return json.RootElement.TryGetProperty("dna", out var dna) && dna.ValueKind == JsonValueKind.Object
               && dna.TryGetProperty("traits", out var traits) && traits.ValueKind == JsonValueKind.Array
            ? traits.EnumerateArray().ToDictionary(t => t.GetProperty("key").GetString()!, t => t.GetProperty("value").GetDouble())
            : [];
    }

    /// <summary>The show followed that year that they were furthest ahead of its other followers on (see the summary).</summary>
    private static async Task<YearCalledItDto?> CalledItAsync(AnalyticsContext db, string userId, int year, TimeZoneInfo zone, CancellationToken ct)
    {
        var from = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, 1, 1), zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year + 1, 1, 1), zone);
        var mine = await db.Follows.AsNoTracking()
            .Where(f => f.UserId == userId && f.FollowedAtUtc >= from && f.FollowedAtUtc < to)
            .Join(db.Shows.Where(s => s.ExternalRating >= CalledItMinRating), f => f.ShowId, s => s.ShowId, (f, s) => new { f.FollowedAtUtc, Show = s })
            .ToListAsync(ct);
        if (mine.Count == 0)
        {
            return null;
        }

        var ids = mine.Select(m => m.Show.ShowId).ToList();
        var others = (await db.Follows.AsNoTracking()
                .Where(f => ids.Contains(f.ShowId) && f.UserId != userId)
                .Select(f => new { f.ShowId, f.FollowedAtUtc })
                .ToListAsync(ct))
            .ToLookup(f => f.ShowId, f => f.FollowedAtUtc);

        return mine
            .Select(m => (m.Show, m.FollowedAtUtc, Others: others[m.Show.ShowId].ToList()))
            .Where(x => x.Others.Count + 1 >= InsightRules.MinUsersToShow)
            .Select(x => (x.Show, x.FollowedAtUtc, Followers: x.Others.Count + 1,
                Ahead: (double)x.Others.Count(t => t > x.FollowedAtUtc) / x.Others.Count))
            .Where(x => x.Ahead >= 0.5)
            .OrderByDescending(x => x.Ahead).ThenByDescending(x => x.Show.ExternalRating)
            .Select(x => new YearCalledItDto(x.Show.ShowId, x.Show.Title, x.Show.PosterUrl, x.FollowedAtUtc, Math.Round(x.Ahead, 2),
                x.Followers, x.Show.ExternalRating))
            .FirstOrDefault();
    }
}
