namespace TvTracker.ShowInsightsEngine;

/// <param name="Enough">At least <see cref="TvClock.MinWatches"/> watches to read a clock from; otherwise only the counts.</param>
/// <param name="Since">The first day counted: a year back, or the first watch when that's later.</param>
/// <param name="BulkLeftOut">Marks of a back catalog left out (<see cref="ShowFactRules.BulkMark"/>+ at once).</param>
/// <param name="Grid">Minutes by weekday (Monday first) and local hour, 7 × 24.</param>
/// <param name="Labels">What kind of viewer the clock says, strongest first; empty when nothing stands out.</param>
/// <param name="Periods">Shares of minutes by part of the day, in day order.</param>
/// <param name="WeekendShare">Saturday and Sunday's share of minutes (2/7 ≈ 0.29 would be even).</param>
/// <param name="Shows">The most watched shows and when each is watched.</param>
public record TvClockDto(bool Enough, DateOnly Since, int Episodes, int Minutes, int BulkLeftOut, IReadOnlyList<IReadOnlyList<int>> Grid,
    IReadOnlyList<ClockLabelDto> Labels, ClockPeakDto? Peak, IReadOnlyList<ClockShareDto> Periods, double WeekendShare, SittingsDto? Sittings,
    IReadOnlyList<ClockShowDto> Shows);

/// <param name="Key">"night-owl", "early-bird", "lunch-break", "prime-time", "weekend-binger" or "weeknight-regular".</param>
/// <param name="Share">The share behind it, 0-1.</param>
public record ClockLabelDto(string Key, string Name, double Share);

/// <param name="Weekday">0 Monday to 6 Sunday.</param>
public record ClockPeakDto(int Weekday, int Hour, int Minutes);

/// <param name="Key">"morning", "daytime", "evening" or "night".</param>
public record ClockShareDto(string Key, string Name, double Share);

/// <param name="AverageEpisodes">Episodes in a sitting, on average.</param>
/// <param name="BingeShare">Sittings of <see cref="TvClock.BingeEpisodes"/> or more episodes, as a share of all.</param>
public record SittingsDto(int Count, double AverageEpisodes, int AverageMinutes, double BingeShare, LongestSittingDto Longest);

/// <param name="ShowId">The show most of it was; <paramref name="Shows"/> counts them all.</param>
public record LongestSittingDto(DateOnly Date, int Episodes, int Minutes, Guid ShowId, string Title, string? PosterUrl, int Shows);

/// <param name="Period">The part of the day most of its minutes fall in (<see cref="ClockShareDto.Key"/>).</param>
/// <param name="Weekend">Mostly watched on Saturdays and Sundays.</param>
public record ClockShowDto(Guid ShowId, string Title, string? PosterUrl, int Minutes, string Period, double PeriodShare, bool Weekend);

/// <summary>
/// Your TV clock: when the caller watches, computed on request from their own watches in the last year (so no minimum
/// counts beyond having enough to say anything), placed in their profile time zone (<c>tz</c>), as Year in TV does.
/// <list type="bullet">
/// <item><b>When a watch happened:</b> a mark is usually made at the end of an episode, so its minutes go to the hour of
/// the mark less half the episode's running time.</item>
/// <item><b>Bulk marks are left out</b> (<see cref="ShowFactRules.BulkMark"/> or more of a show at once): they say when a back
/// catalog was entered, not watched.</item>
/// <item><b>Labels</b> compare shares with fixed thresholds: night (22:00-05:00) 35%+, morning (05:00-11:00) 30%+, lunch
/// (11:00-14:00) 30%+, prime time (19:00-23:00) 50%+, weekends 55%+ (an even week is 29%), weeknights 85%+.</item>
/// <item><b>Sittings:</b> marks in a row, any show, each within the next episode's running time plus
/// <see cref="SittingGap"/> of the one before. A binge is <see cref="BingeEpisodes"/>+ episodes in one.</item>
/// </list>
/// </summary>
public static class TvClock
{
    public const int MinWatches = 10;

    public const int BingeEpisodes = 3;

    private const int MaxShows = 5;

    private const int DefaultRuntime = 30;

    private static readonly TimeSpan SittingGap = TimeSpan.FromMinutes(30);

    private static readonly (string Key, string Name, int From, int To)[] Parts =
    [
        ("morning", "Mornings", 5, 11), ("daytime", "Daytime", 11, 17), ("evening", "Evenings", 17, 22), ("night", "Late nights", 22, 5)
    ];

    internal sealed record Watch(Guid ShowId, DateTime MarkedAtUtc, DateTime Local, int Minutes);

    /// <summary>
    /// A mark placed in the viewer's day: at the mark less half the episode's running time (<see cref="DefaultRuntime"/>
    /// when it's unknown), in their time zone.
    /// </summary>
    internal static Watch Placed(Guid showId, DateTime markedAtUtc, int? runtimeMinutes, TimeZoneInfo zone)
    {
        var minutes = runtimeMinutes is > 0 ? runtimeMinutes.Value : DefaultRuntime;
        return new Watch(showId, markedAtUtc, ViewerRules.Local(markedAtUtc.AddMinutes(-minutes / 2.0), zone), minutes);
    }

    /// <summary>
    /// The clock the watches make (in the order they were marked): only the counts when there are fewer than
    /// <see cref="MinWatches"/>, in which case <paramref name="shows"/> isn't needed.
    /// </summary>
    /// <param name="since">The first day counted.</param>
    /// <param name="shows">The watched shows' titles and posters; one that's gone is missing.</param>
    internal static TvClockDto Of(List<Watch> watches, DateOnly since, int bulkLeftOut,
        IReadOnlyDictionary<Guid, (string Title, string? PosterUrl)> shows)
    {
        var total = watches.Sum(w => w.Minutes);
        if (watches.Count < MinWatches)
        {
            var empty = Enumerable.Range(0, 7).Select(_ => (IReadOnlyList<int>)new int[24]).ToList();
            return new TvClockDto(false, since, watches.Count, total, bulkLeftOut, empty, [], null, [], 0, null, []);
        }

        var grid = new int[7, 24];
        foreach (var w in watches)
        {
            grid[Weekday(w.Local), w.Local.Hour] += w.Minutes;
        }
        double Share(Func<Watch, bool> where) => Math.Round((double)watches.Where(where).Sum(w => w.Minutes) / total, 2);

        var periods = Parts.Select(p => new ClockShareDto(p.Key, p.Name, Share(w => PartOf(w.Local.Hour) == p.Key))).ToList();
        var weekend = Share(w => Weekday(w.Local) >= 5);
        var candidates = new List<ClockLabelDto>
        {
            new("night-owl", "Night owl", periods.Single(p => p.Key == "night").Share),
            new("early-bird", "Early bird", periods.Single(p => p.Key == "morning").Share),
            new("lunch-break", "Lunch-break viewer", Share(w => w.Local.Hour is >= 11 and < 14)),
            new("prime-time", "Prime-timer", Share(w => w.Local.Hour is >= 19 and < 23)),
            new("weekend-binger", "Weekend binger", weekend),
            new("weeknight-regular", "Weeknight regular", Math.Round(1 - weekend, 2))
        };
        var thresholds = new Dictionary<string, double>
        {
            ["night-owl"] = 0.35,
            ["early-bird"] = 0.30,
            ["lunch-break"] = 0.30,
            ["prime-time"] = 0.50,
            ["weekend-binger"] = 0.55,
            ["weeknight-regular"] = 0.85
        };
        // Strongest first: by how far past its threshold, relative to it.
        var labels = candidates.Where(l => l.Share >= thresholds[l.Key])
            .OrderByDescending(l => l.Share / thresholds[l.Key])
            .Take(2)
            .ToList();

        var peak = Enumerable.Range(0, 7).SelectMany(d => Enumerable.Range(0, 24).Select(h => new ClockPeakDto(d, h, grid[d, h])))
            .MaxBy(c => c.Minutes);

        var top = watches.Where(w => shows.ContainsKey(w.ShowId))
            .GroupBy(w => w.ShowId)
            .OrderByDescending(g => g.Sum(w => w.Minutes))
            .Take(MaxShows)
            .Select(g =>
            {
                var minutes = g.Sum(w => w.Minutes);
                var part = g.GroupBy(w => PartOf(w.Local.Hour)).MaxBy(p => p.Sum(w => w.Minutes))!;
                var show = shows[g.Key];
                return new ClockShowDto(g.Key, show.Title, show.PosterUrl, minutes, part.Key, Math.Round((double)part.Sum(w => w.Minutes) / minutes, 2),
                    g.Where(w => Weekday(w.Local) >= 5).Sum(w => w.Minutes) * 2 > minutes);
            })
            .ToList();

        return new TvClockDto(true, since, watches.Count, total, bulkLeftOut,
            Enumerable.Range(0, 7).Select(d => (IReadOnlyList<int>)Enumerable.Range(0, 24).Select(h => grid[d, h]).ToArray()).ToList(),
            labels, peak, periods, weekend, Sittings(watches, shows), top);
    }

    /// <summary>
    /// Marks in a row grouped into sittings, and what they add up to.
    /// </summary>
    private static SittingsDto? Sittings(List<Watch> watches, IReadOnlyDictionary<Guid, (string Title, string? PosterUrl)> shows)
    {
        var sittings = new List<List<Watch>>();
        foreach (var w in watches)
        {
            if (sittings.Count > 0 && w.MarkedAtUtc - sittings[^1][^1].MarkedAtUtc <= TimeSpan.FromMinutes(w.Minutes) + SittingGap)
            {
                sittings[^1].Add(w);
            }
            else
            {
                sittings.Add([w]);
            }
        }

        var longest = sittings.OrderByDescending(s => s.Count).ThenByDescending(s => s.Sum(w => w.Minutes))
            .FirstOrDefault(s => shows.ContainsKey(s.GroupBy(w => w.ShowId).MaxBy(g => g.Count())!.Key));
        if (longest is null)
        {
            return null;
        }
        var main = longest.GroupBy(w => w.ShowId).MaxBy(g => g.Count())!.Key;
        var (title, poster) = shows[main];
        return new SittingsDto(sittings.Count, Math.Round(sittings.Average(s => s.Count), 1), (int)Math.Round(sittings.Average(s => s.Sum(w => w.Minutes))),
            Math.Round((double)sittings.Count(s => s.Count >= BingeEpisodes) / sittings.Count, 2),
            new LongestSittingDto(DateOnly.FromDateTime(longest[0].Local), longest.Count, longest.Sum(w => w.Minutes), main, title, poster,
                longest.Select(w => w.ShowId).Distinct().Count()));
    }

    /// <summary>
    /// 0 Monday to 6 Sunday.
    /// </summary>
    private static int Weekday(DateTime local) => ViewerRules.Weekday(local.DayOfWeek);

    private static string PartOf(int hour) => hour switch
    {
        >= 5 and < 11 => "morning",
        >= 11 and < 17 => "daytime",
        >= 17 and < 22 => "evening",
        _ => "night"
    };
}
