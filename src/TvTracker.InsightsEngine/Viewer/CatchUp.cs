namespace TvTracker.InsightsEngine;

/// <param name="Premiere">The new season the user is catching up for.</param>
/// <param name="Full">Everything aired after the furthest episode watched (from the start for a show not begun).</param>
/// <param name="Cut">The same stretch in the essential cut ("lean"), when the show can be cut and it saves time.</param>
/// <param name="YourMinutesPerWeek">The user's watching across all shows over the last <see cref="CatchUp.PaceWeeks"/>
/// weeks; 0 when they've watched nothing in that time.</param>
/// <param name="Path">The path the <paramref name="Weeks"/> follow: "full", or "cut" when asked for and available.</param>
/// <param name="Weeks">The episodes to watch each week, spread evenly up to the premiere.</param>
public record CatchUpDto(
    Guid ShowId,
    string Title,
    string? PosterUrl,
    CatchUpPremiereDto Premiere,
    int DaysLeft,
    CatchUpPathDto Full,
    CatchUpPathDto? Cut,
    int YourMinutesPerWeek,
    string Path,
    IReadOnlyList<CatchUpWeekDto> Weeks);

public record CatchUpPremiereDto(int SeasonNumber, DateTimeOffset PremieresAtUtc);

/// <param name="Verdict">Against the user's pace: "comfortable" (half of it or less), "doable" (up to all of it), "tight"
/// (up to 1.5×), "behind" (more), or null without a pace.</param>
/// <param name="StartBy">The latest day to start and still finish at the user's pace; null without a pace.</param>
public record CatchUpPathDto(int Episodes, int Minutes, double EpisodesPerWeek, int MinutesPerWeek, string? Verdict, DateOnly? StartBy);

public record CatchUpWeekDto(DateOnly Starts, int Minutes, IReadOnlyList<CatchUpEpisodeDto> Episodes);

public record CatchUpEpisodeDto(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, int Minutes);

/// <summary>
/// The catch-up planner: for a followed show with a new season on the way, what's left to watch before it starts and
/// the pace that gets there, measured against the user's own watching across all their shows. Where the essential cut
/// makes the stretch noticeably shorter, it's offered as the other path. Computed on request from the caller's own
/// watches, like "your taste".
/// </summary>
public static class CatchUp
{
    /// <summary>
    /// Premieres this far ahead, or nearer, are worth planning for.
    /// </summary>
    public const int HorizonDays = 120;

    /// <summary>
    /// The user's pace is their watching over this many weeks.
    /// </summary>
    public const int PaceWeeks = 8;

    /// <summary>
    /// A day counts towards the pace for at most this many minutes. Marking a show's past seasons at once ("I watched
    /// these years ago") lands on one day, and would otherwise read as a 90-episode binge.
    /// </summary>
    internal const int MaxMinutesPerDay = 240;

    /// <summary>
    /// The cut is offered when it saves at least this share of the time.
    /// </summary>
    private const double MinCutSaving = 0.1;

    /// <param name="Watches">The marks the pace rests on; a pace of 0 with watches means episodes without running times.</param>
    internal sealed record Pace(int MinutesPerWeek, int Watches);

    /// <summary>
    /// The pace from the viewer's days of watching over the last <see cref="PaceWeeks"/> weeks: each day's minutes,
    /// capped at <see cref="MaxMinutesPerDay"/>, and how many marks it had.
    /// </summary>
    internal static Pace PaceOf(IReadOnlyCollection<(int Minutes, int Count)> days) =>
        new(days.Sum(d => Math.Min(d.Minutes, MaxMinutesPerDay)) / PaceWeeks, days.Sum(d => d.Count));

    /// <summary>
    /// The plan for one show; null when no new season is due within <see cref="HorizonDays"/> days, or the viewer is
    /// already caught up.
    /// </summary>
    /// <param name="pace">The viewer's minutes a week (<see cref="PaceOf"/>).</param>
    /// <param name="preferCut">Lay out the essential cut's path week by week, when it's offered.</param>
    internal static CatchUpDto? Build(ShowLoad load, NextPremiere? next, int pace, bool preferCut, DateTimeOffset now)
    {
        if (next is null || next.PremieresAtUtc > now.AddDays(HorizonDays))
        {
            return null;
        }
        var premiere = new CatchUpPremiereDto(next.SeasonNumber, next.PremieresAtUtc);

        var show = load.Show;
        var aired = load.Aired;
        var (_, ahead) = ViewerRules.Split(aired, load.Watched);
        if (ahead.Count == 0)
        {
            return null;
        }

        var minutes = load.Minutes;
        var rating = load.Rating;
        var notes = load.Notes;
        var decisions = EssentialCut.Unavailable(aired, rating, notes) is null
            ? EssentialCut.Decide(show, load.Episodes, aired, rating, EssentialCut.Lean, now, notes)
            : null;
        var kept = decisions is null ? null : ahead.Where(e => decisions[e.EpisodeId].Keep).ToList();

        var daysLeft = Math.Max(1, (int)Math.Ceiling((premiere.PremieresAtUtc - now).TotalDays));
        var full = PathOf(ahead, minutes, daysLeft, pace, premiere.PremieresAtUtc);
        var cut = kept is not null && kept.Sum(minutes) <= full.Minutes * (1 - MinCutSaving)
            ? PathOf(kept, minutes, daysLeft, pace, premiere.PremieresAtUtc)
            : null;
        var path = preferCut && cut is not null ? "cut" : "full";

        return new CatchUpDto(show.ShowId, show.Title, show.PosterUrl, premiere, daysLeft, full, cut, pace, path,
            Weeks(path == "cut" ? kept! : ahead, minutes, daysLeft, now));
    }

    private static CatchUpPathDto PathOf(List<EpisodeCopy> path, Func<EpisodeCopy, int> minutes, int daysLeft, int pace, DateTimeOffset premiere)
    {
        var total = path.Sum(minutes);
        var weeks = daysLeft / 7.0;
        var perWeek = (int)Math.Ceiling(total / weeks);
        string? verdict = pace <= 0 ? null
            : perWeek <= pace * 0.5 ? "comfortable"
            : perWeek <= pace ? "doable"
            : perWeek <= pace * 1.5 ? "tight"
            : "behind";
        DateOnly? startBy = pace > 0 ? DateOnly.FromDateTime(premiere.UtcDateTime.AddDays(-Math.Ceiling(total * 7.0 / pace))) : null;
        return new CatchUpPathDto(path.Count, total, Math.Round(path.Count / weeks, 1), perWeek, verdict, startBy);
    }

    /// <summary>
    /// The path split into weeks up to the premiere, by running time rather than count, so long episodes spread out.
    /// </summary>
    private static List<CatchUpWeekDto> Weeks(List<EpisodeCopy> path, Func<EpisodeCopy, int> minutes, int daysLeft, DateTimeOffset now)
    {
        var weekCount = Math.Max(1, daysLeft / 7);
        var total = path.Sum(minutes);
        var weeks = new List<CatchUpWeekDto>();
        var done = 0;
        var index = 0;
        for (var week = 0; week < weekCount && index < path.Count; week++)
        {
            // Each week ends where the running total reaches its share of the whole; the last takes what's left.
            var target = week == weekCount - 1 ? int.MaxValue : (int)Math.Round(total * (week + 1.0) / weekCount);
            var items = new List<EpisodeCopy>();
            while (index < path.Count && (items.Count == 0 || done + minutes(path[index]) / 2 <= target))
            {
                done += minutes(path[index]);
                items.Add(path[index++]);
            }
            weeks.Add(new CatchUpWeekDto(DateOnly.FromDateTime(now.UtcDateTime.AddDays(7 * week)), items.Sum(minutes),
                items.Select(e => new CatchUpEpisodeDto(e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.Title, minutes(e))).ToList()));
        }
        return weeks;
    }
}
