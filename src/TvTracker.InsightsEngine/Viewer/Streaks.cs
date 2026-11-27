namespace TvTracker.InsightsEngine;

/// <param name="KeptUpDays">The window for keeping up, so the page states the rule the figures follow.</param>
/// <param name="BulkLeftOut">Marks of a back catalog left out (<see cref="ShowFactRules.BulkMark"/>+ at once).</param>
/// <param name="Days">Days in a row with something watched.</param>
/// <param name="Weeks">Weeks (Monday to Sunday) in a row with something watched; the longest run's dates are its first Monday
/// and last Sunday.</param>
/// <param name="KeptUp">Shows whose new episodes the caller watches within <see cref="Streaks.KeptUpDays"/> days of airing,
/// longest current run first.</param>
/// <param name="Due">Aired episodes still inside their window that would extend a kept-up run, soonest deadline first.</param>
/// <param name="Best">The longest kept-up run ever, on any show.</param>
public record StreaksDto(int KeptUpDays, int BulkLeftOut, StreakDto Days, StreakDto Weeks, IReadOnlyList<KeptUpDto> KeptUp,
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
/// <item><b>Bulk marks are left out</b> (<see cref="ShowFactRules.BulkMark"/> or more of a show at once): they say when a back
/// catalog was entered, not when it was watched.</item>
/// <item><b>No titles:</b> episodes are given by number, as Spoiler watch does; a due episode is by definition unseen.</item>
/// </list>
/// </summary>
public static class Streaks
{
    public const int KeptUpDays = 7;

    internal const int MinKeptUp = 2;

    private static readonly TimeSpan Early = TimeSpan.FromDays(1);

    internal sealed record Aired(Guid EpisodeId, Guid ShowId, int Season, int Episode, DateTimeOffset AirsAtUtc);

    /// <summary>
    /// One show's kept-up record: the current and longest runs, the episodes that end them, and what's still due.
    /// </summary>
    /// <param name="Due">Aired episodes still inside their window, with their deadlines, oldest first.</param>
    internal sealed record KeptUpRun(int Current, int Longest, Aired? Latest, Aired? LongestEnd, IReadOnlyList<(Aired Episode, DateTimeOffset Deadline)> Due);

    /// <summary>
    /// One show's aired regular episodes, each kept (watched within <see cref="KeptUpDays"/> of airing, or up to a day
    /// before), missed, or still due, in air order from the first kept one on: episodes before it don't break anything.
    /// </summary>
    internal static KeptUpRun KeptUp(IEnumerable<Aired> episodes, IReadOnlyDictionary<Guid, DateTimeOffset> watchedAt, DateTimeOffset now)
    {
        // Kept (true), missed (false) or still due (null).
        var states = episodes.OrderBy(e => e.AirsAtUtc).ThenBy(e => e.Season).ThenBy(e => e.Episode)
            .Select(e =>
            {
                var deadline = e.AirsAtUtc.AddDays(KeptUpDays);
                bool? kept = watchedAt.TryGetValue(e.EpisodeId, out var w) ? w >= e.AirsAtUtc - Early && w <= deadline
                    : deadline > now ? null : false;
                return (Episode: e, Kept: kept, Deadline: deadline);
            })
            .SkipWhile(s => s.Kept != true)
            .ToList();
        var decided = states.Where(s => s.Kept is not null).ToList();

        int longest = 0, run = 0;
        Aired? longestEnd = null;
        foreach (var s in decided)
        {
            run = s.Kept == true ? run + 1 : 0;
            if (run > longest)
            {
                (longest, longestEnd) = (run, s.Episode);
            }
        }
        var current = decided.AsEnumerable().Reverse().TakeWhile(s => s.Kept == true).Count();
        var latest = current > 0 ? decided[^1].Episode : null;
        return new KeptUpRun(current, longest, latest, longestEnd, states.Where(s => s.Kept is null).Select(s => (s.Episode, s.Deadline)).ToList());
    }

    /// <summary>
    /// The Monday a date's week starts on.
    /// </summary>
    internal static DateOnly WeekStart(DateOnly day) => day.AddDays(-ViewerRules.Weekday(day.DayOfWeek));

    /// <summary>
    /// The current and longest runs of consecutive units (days, or weeks by their Monday) in <paramref name="dates"/>; the
    /// current one ends on <paramref name="now"/>'s unit or the one before.
    /// </summary>
    internal static StreakDto Runs(List<DateOnly> dates, Func<DateOnly, int> unit, int now)
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

    /// <summary>
    /// The day and week streaks, from the local dates with a watch. A run of weeks ends on its last week's Sunday, not
    /// the Monday that starts it.
    /// </summary>
    internal static (StreakDto Days, StreakDto Weeks) Calendar(IEnumerable<DateOnly> watchedOn, DateOnly today)
    {
        var days = watchedOn.Distinct().ToList();
        var weeks = Runs(days.Select(WeekStart).Distinct().ToList(), d => d.DayNumber / 7, WeekStart(today).DayNumber / 7);
        return (Runs(days, d => d.DayNumber, today.DayNumber), weeks with { LongestTo = weeks.LongestTo?.AddDays(6) });
    }

    /// <summary>
    /// How many shows the kept-up list and the due list each show at most.
    /// </summary>
    public const int MaxShows = 12;

    /// <summary>
    /// What is shown of <see cref="AcrossShows"/>' answer, with the shows named: the kept-up shows longest current run
    /// first (then by title), and what's due soonest first (then the longer run, then the earlier episode), each up to
    /// <see cref="MaxShows"/>. A show that's gone from the catalog is left out.
    /// </summary>
    /// <param name="names">Title and poster of the shows with a streak.</param>
    internal static (List<KeptUpDto> KeptUp, List<StreakDueDto> Due, KeptUpDto? Best) Shown(IEnumerable<KeptUpDto> keptUp, IEnumerable<StreakDueDto> due,
        KeptUpDto? best, IReadOnlyDictionary<Guid, (string Title, string? PosterUrl)> names)
    {
        KeptUpDto Name(KeptUpDto k) => k with { Title = names[k.ShowId].Title, PosterUrl = names[k.ShowId].PosterUrl };
        return (
            keptUp.Where(k => names.ContainsKey(k.ShowId)).Select(Name)
                .OrderByDescending(k => k.Current).ThenBy(k => k.Title, StringComparer.Ordinal).ThenBy(k => k.ShowId).Take(MaxShows).ToList(),
            due.Where(d => names.ContainsKey(d.ShowId))
                .Select(d => d with { Title = names[d.ShowId].Title, PosterUrl = names[d.ShowId].PosterUrl })
                .OrderBy(d => d.WatchBy).ThenByDescending(d => d.Streak).ThenBy(d => d.Season).ThenBy(d => d.Episode).ThenBy(d => d.ShowId)
                .Take(MaxShows).ToList(),
            best is not null && names.ContainsKey(best.ShowId) ? Name(best) : null);
    }

    /// <summary>
    /// Every show's kept-up record (<see cref="KeptUp"/>): the runs of <see cref="MinKeptUp"/> or more still alive, what's
    /// due to keep them, and the longest run ever. Unordered and unnamed: <see cref="Shown"/> does both.
    /// </summary>
    internal static (List<KeptUpDto> KeptUp, List<StreakDueDto> Due, KeptUpDto? Best) AcrossShows(IEnumerable<Aired> aired,
        IReadOnlyDictionary<Guid, DateTimeOffset> watchedAt, DateTimeOffset now, TimeZoneInfo zone)
    {
        var keptUp = new List<KeptUpDto>();
        var due = new List<StreakDueDto>();
        KeptUpDto? best = null;
        foreach (var show in aired.GroupBy(e => e.ShowId))
        {
            var run = KeptUp(show, watchedAt, now);
            if (run.Current >= MinKeptUp)
            {
                keptUp.Add(new KeptUpDto(show.Key, "", null, run.Current, run.Longest, run.Latest!.Season, run.Latest.Episode));
                due.AddRange(run.Due.Select(d => new StreakDueDto(show.Key, "", null, d.Episode.Season, d.Episode.Episode,
                    ViewerRules.LocalDate(d.Deadline, zone), run.Current)));
            }
            if (run.Longest >= MinKeptUp && (best is null || run.Longest > best.Longest))
            {
                best = new KeptUpDto(show.Key, "", null, run.Current, run.Longest, run.LongestEnd!.Season, run.LongestEnd.Episode);
            }
        }
        return (keptUp, due, best);
    }
}
