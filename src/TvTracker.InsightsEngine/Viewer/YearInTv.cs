using System.Globalization;
using TvTracker.IntegrationEvents;

namespace TvTracker.InsightsEngine;

/// <param name="Year">The year shown.</param>
/// <param name="Years">Years with viewing, latest first, for switching.</param>
/// <param name="Bulk">Episodes left out because they were marked many at once (see <see cref="ShowFactRules.BulkMark"/>).</param>
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
/// <item><b>Bulk marks are left out:</b> a mark of <see cref="ShowFactRules.BulkMark"/> or more of a show's episodes at once ("watched up
/// to S05E10") says when they were marked, not watched, and seeding a back catalog would read as a record year. The
/// count left out is returned, so the page can say so.</item>
/// <item><b>Taste shift:</b> each DNA trait averaged over the year's shows, weighted by minutes watched, against the year
/// before's; traits that moved <see cref="MinShift"/> or more are named, largest first.</item>
/// <item><b>Called it:</b> of the shows they followed that year with a rating of 7.5 or more, the one they followed earliest
/// relative to its other followers here, when they were ahead of at least half of them.</item>
/// </list>
/// </summary>
public static class YearInTv
{
    /// <summary>
    /// A trait is named in the shift when the minutes-weighted average moved at least this much.
    /// </summary>
    public const double MinShift = 0.1;

    private const int MaxShows = 5;

    private const int MaxListed = 8;

    private const int MaxShifts = 3;

    internal const double CalledItMinRating = 7.5;

    internal sealed record Watch(Guid ShowId, Guid EpisodeId, DateTime WatchedAtUtc, DateOnly Day, int Minutes, int Season, int Number,
        string Title, double? Rating);

    /// <summary>
    /// Most consecutive days in the set.
    /// </summary>
    internal static int LongestStreak(IEnumerable<DateOnly> days) => Streaks.Runs(days.Distinct().ToList(), d => d.DayNumber, 0).Longest;

    /// <param name="Shown">The year recapped.</param>
    /// <param name="Years">The years with viewing, latest first.</param>
    internal sealed record Slice(int Shown, IReadOnlyList<int> Years, List<Watch> InYear, List<Watch> LastYear);

    /// <summary>
    /// The year to recap and its watches: the year asked for (when it is one they could have a recap of: from their
    /// first year with viewing to this one), else the latest with viewing, else this one; with the year before it, for
    /// what shifted.
    /// </summary>
    internal static Slice Split(IReadOnlyCollection<Watch> watches, int? year, DateOnly today)
    {
        var years = watches.Select(w => w.Day.Year).Distinct().OrderDescending().ToList();
        var latest = years.Count > 0 ? years[0] : today.Year;
        var shown = year is { } asked && asked >= Math.Min(years.LastOrDefault(latest), today.Year) && asked <= today.Year ? asked : latest;
        return new Slice(shown, years, watches.Where(w => w.Day.Year == shown).ToList(), watches.Where(w => w.Day.Year == shown - 1).ToList());
    }

    /// <summary>
    /// The moment "left behind" is judged from: now for the year under way, the year's last second for one gone by.
    /// </summary>
    internal static DateTime AsOf(int shown, DateOnly today, DateTimeOffset now) =>
        shown == today.Year ? now.UtcDateTime : new DateTime(shown, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    /// <summary>
    /// Finished (ended shows, last regular episode watched this year), caught up (running shows, every aired regular
    /// episode watched, the last of them this year) and left behind (episodes left, nothing watched for
    /// <see cref="InsightRules.StoppedAfterDays"/> days by <paramref name="asOf"/>); each most watched first.
    /// </summary>
    /// <param name="ids">The shows watched in the year.</param>
    /// <param name="aired">Their aired regular episodes.</param>
    /// <param name="allWatches">Every watch of the viewer's, bulk marks included (a bulk-marked show is finished all the same).</param>
    internal static (List<Guid> Finished, List<Guid> CaughtUp, List<Guid> LeftBehind) Progress(IReadOnlyCollection<Guid> ids,
        ILookup<Guid, (Guid EpisodeId, DateTimeOffset? AirsAtUtc)> aired, IEnumerable<(Guid ShowId, Guid EpisodeId, DateTime WatchedAtUtc)> allWatches,
        IReadOnlyDictionary<Guid, ShowCopy> shows, IReadOnlyCollection<Watch> inYear, DateTime asOf)
    {
        var watched = allWatches.Where(w => ids.Contains(w.ShowId)).ToLookup(w => w.ShowId);
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
            var ended = shows[id].HasEnded();
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

    /// <summary>
    /// The DNA traits that moved most between the year before and this one (minutes-weighted).
    /// </summary>
    /// <param name="dna">The watched shows' DNA traits by key.</param>
    internal static List<YearShiftDto> Shift(IReadOnlyCollection<Watch> inYear, IReadOnlyCollection<Watch> lastYear,
        IReadOnlyDictionary<Guid, Dictionary<string, double>> dna)
    {
        if (inYear.Count == 0 || lastYear.Count == 0)
        {
            return [];
        }

        Dictionary<string, double> Average(IEnumerable<Watch> watches)
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

    /// <summary>
    /// The show followed that year that the viewer was furthest ahead of its other followers on (see the summary).
    /// </summary>
    /// <param name="mine">The well-rated shows (<see cref="CalledItMinRating"/>+) they followed that year, and when.</param>
    /// <param name="others">When everyone else followed each of them.</param>
    internal static YearCalledItDto? CalledIt(IEnumerable<(ShowCopy Show, DateTime FollowedAtUtc)> mine, ILookup<Guid, DateTime> others) => mine
        .Select(m => (m.Show, m.FollowedAtUtc, Others: others[m.Show.ShowId].ToList()))
        .Where(x => x.Others.Count + 1 >= InsightRules.MinUsersToShow)
        .Select(x => (x.Show, x.FollowedAtUtc, Followers: x.Others.Count + 1,
            Ahead: (double)x.Others.Count(t => t > x.FollowedAtUtc) / x.Others.Count))
        .Where(x => x.Ahead >= 0.5)
        .OrderByDescending(x => x.Ahead).ThenByDescending(x => x.Show.ExternalRating)
        .Select(x => new YearCalledItDto(x.Show.ShowId, x.Show.Title, x.Show.PosterUrl, x.FollowedAtUtc, Math.Round(x.Ahead, 2),
            x.Followers, x.Show.ExternalRating))
        .FirstOrDefault();

    /// <summary>
    /// The year's recap.
    /// </summary>
    /// <param name="years">The years with viewing, latest first.</param>
    /// <param name="watches">Every watch of the viewer's outside bulk marks, in their calendar.</param>
    /// <param name="inYear">Those of the year shown.</param>
    /// <param name="shows">The shows watched in it; one that's gone from the catalog is left out.</param>
    /// <param name="bulkInYear">Bulk marks left out of the year.</param>
    /// <param name="progress">What they finished, caught up on and left behind (<see cref="Progress"/>).</param>
    internal static YearInTvDto Of(int shown, IReadOnlyList<int> years, IReadOnlyCollection<Watch> watches, IReadOnlyCollection<Watch> inYear,
        IReadOnlyDictionary<Guid, ShowCopy> shows, int bulkInYear, (List<Guid> Finished, List<Guid> CaughtUp, List<Guid> LeftBehind) progress,
        IReadOnlyList<YearShiftDto> shift, YearCalledItDto? calledIt)
    {
        var byShow = inYear.GroupBy(w => w.ShowId).Where(g => shows.ContainsKey(g.Key)).ToList();
        YearShowDto ShowOf(Guid id, IEnumerable<Watch> ws) =>
            new(id, shows[id].Title, shows[id].PosterUrl, ws.Count(), ws.Sum(w => w.Minutes));
        YearShowDto Listed(Guid id) => ShowOf(id, byShow.First(g => g.Key == id));

        var minutes = inYear.Sum(w => w.Minutes);
        var firstWatch = watches.GroupBy(w => w.ShowId).ToDictionary(g => g.Key, g => g.Min(w => w.Day));
        var days = inYear.GroupBy(w => w.Day).Select(g => new YearDayDto(g.Key, g.Count(), g.Sum(w => w.Minutes))).ToList();

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

        return new YearInTvDto(
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
            progress.Finished.Select(Listed).Take(MaxListed).ToList(),
            progress.CaughtUp.Select(Listed).Take(MaxListed).ToList(),
            progress.LeftBehind.Select(Listed).Take(MaxListed).ToList(),
            best is null ? null : new YearEpisodeDto(best.ShowId, shows[best.ShowId].Title, shows[best.ShowId].PosterUrl, best.Season,
                best.Number, best.Title, best.Rating!.Value),
            shift,
            calledIt);
    }
}
