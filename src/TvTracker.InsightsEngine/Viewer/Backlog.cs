namespace TvTracker.InsightsEngine;

/// <param name="Minutes">Everything on the list: shows not started, and what's left of dropped ones.</param>
/// <param name="YourMinutesPerWeek">The user's pace, as the catch-up planner measures it.</param>
/// <param name="WeeksToClear">At that pace; null without one.</param>
/// <param name="LetGoMinutes">What letting go of the "let-it-go" shows, and dropped shows that only get worse, saves.</param>
/// <param name="CutSavesMinutes">What watching the long shows in their essential cut saves.</param>
/// <param name="Dropped">Shows the user dropped, with whether they're worth going back to.</param>
public record BacklogDto(
    int Minutes,
    int YourMinutesPerWeek,
    int? WeeksToClear,
    int LetGoMinutes,
    int CutSavesMinutes,
    IReadOnlyList<BacklogShowDto> Shows,
    IReadOnlyList<ComebackDto> Dropped);

/// <param name="Value">How much it's likely worth the user's time, on the rating's 0-10 scale (see ShowValue).</param>
/// <param name="Verdict">"start-next", "worth-it", "long" (<see cref="Backlog.LongMinutes"/> or more; the essential cut
/// is offered), "ends-badly", or "let-it-go".</param>
/// <param name="CutMinutes">The essential cut's length, for long shows that have one.</param>
public record BacklogShowDto(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes, int? CutMinutes, double Value,
    string Verdict, IReadOnlyList<string> Reasons);

/// <summary>
/// Backlog triage: the followed shows the user hasn't started, ranked by how much they're likely worth the user's time,
/// with a plain verdict each, plus the shows they dropped and whether to go back. Analytics doesn't know Tracking's
/// "plan to watch" status, so the backlog is followed shows with nothing watched, which is what that status mostly means.
/// Computed on request from the caller's own data.
/// </summary>
public static class Backlog
{
    /// <summary>
    /// Shows this long or longer are "long": worth it, but the essential cut is offered.
    /// </summary>
    public const int LongMinutes = 40 * 60;

    /// <summary>
    /// Below this value, a show is "let it go".
    /// </summary>
    private const double LetGoBelow = 7.0;

    /// <summary>
    /// A show that ends badly is still worth starting from this value.
    /// </summary>
    private const double EndsBadlyBelow = 8.0;

    private const int StartNext = 3;

    private static readonly string[] Order = ["start-next", "worth-it", "long", "ends-badly", "let-it-go"];

    /// <summary>
    /// A followed, unstarted show with how much it's worth to the viewer and what to do about it.
    /// </summary>
    /// <summary>
    /// The verdict of a show worth watching that runs <see cref="LongMinutes"/> or more: the lean cut's length is worked out for these.
    /// </summary>
    internal const string Long = "long";

    internal sealed record Assessed(ShowCopy Show, int Episodes, int Minutes, ShowValue.Assessment Assessment, string Verdict);

    /// <summary>
    /// Each show with something aired, assessed (<see cref="ShowValue"/>) and given its verdict; "start-next" is decided
    /// in <see cref="Of"/>, once they're all known.
    /// </summary>
    /// <param name="runs">Aired regular episodes and their running time, per show.</param>
    internal static List<Assessed> Assess(IEnumerable<ShowCopy> shows, IReadOnlyDictionary<Guid, (int Episodes, int Minutes)> runs,
        IReadOnlyDictionary<Guid, ShowInsights> insights, HashSet<string> liked) =>
        shows.Where(s => runs[s.ShowId].Episodes > 0)
            .Select(show =>
            {
                var assessment = ShowValue.Assess(show, insights.GetValueOrDefault(show.ShowId), liked);
                var verdict = assessment.Value < LetGoBelow ? "let-it-go"
                    : assessment.Landing is LandingVerdicts.Fizzles or LandingVerdicts.FinaleLetsDown && assessment.Value < EndsBadlyBelow ? "ends-badly"
                    : runs[show.ShowId].Minutes >= LongMinutes ? Long
                    : "worth-it";
                return new Assessed(show, runs[show.ShowId].Episodes, runs[show.ShowId].Minutes, assessment, verdict);
            })
            .ToList();

    /// <summary>
    /// The backlog: the assessed shows in the order to deal with them, the dropped ones, and what it all adds up to.
    /// </summary>
    /// <param name="cuts">The long shows' episodes, ratings and notes, for the lean cut's running time.</param>
    /// <param name="dropped">The shows the viewer dropped (<see cref="Comeback"/>).</param>
    /// <param name="pace">The viewer's minutes a week (<see cref="CatchUp.PaceOf"/>).</param>
    internal static BacklogDto Of(IEnumerable<Assessed> assessed, IReadOnlyDictionary<Guid, ShowLoad> cuts, IReadOnlyList<ComebackDto> dropped,
        int pace, DateTimeOffset now)
    {
        var items = assessed
            .Select(a => new BacklogShowDto(a.Show.ShowId, a.Show.Title, a.Show.PosterUrl, a.Episodes, a.Minutes,
                cuts.TryGetValue(a.Show.ShowId, out var load) ? CutMinutes(load, now) : null, a.Assessment.Value, a.Verdict, a.Assessment.Reasons))
            .ToList();

        // The best few that aren't long go first.
        var next = items.Where(i => i.Verdict == "worth-it").OrderByDescending(i => i.Value).Take(StartNext).Select(i => i.ShowId).ToHashSet();
        items = items
            .Select(i => next.Contains(i.ShowId) ? i with { Verdict = "start-next" } : i)
            .OrderBy(i => Array.IndexOf(Order, i.Verdict))
            .ThenByDescending(i => i.Value)
            .ToList();

        var total = items.Sum(i => i.Minutes) + dropped.Sum(d => d.AheadMinutes);
        var letGo = items.Where(i => i.Verdict == "let-it-go").Sum(i => i.Minutes) + dropped.Where(d => d.Verdict == "worse").Sum(d => d.AheadMinutes);
        var cutSaves = items.Where(i => i.CutMinutes is not null).Sum(i => i.Minutes - i.CutMinutes!.Value);
        return new BacklogDto(total, pace, pace > 0 ? (int)Math.Ceiling((double)total / pace) : null, letGo, cutSaves, items, dropped);
    }

    /// <summary>
    /// The "lean" essential cut's running time; null when the show can't be cut or it saves nothing.
    /// </summary>
    private static int? CutMinutes(ShowLoad load, DateTimeOffset now)
    {
        var aired = load.Aired;
        if (EssentialCut.Unavailable(aired, load.Rating, load.Notes) is not null)
        {
            return null;
        }

        var decisions = EssentialCut.Decide(load.Show, load.Episodes, aired, load.Rating, EssentialCut.Lean, now, load.Notes);
        var kept = aired.Where(e => decisions[e.EpisodeId].Keep).Sum(load.Minutes);
        return kept < aired.Sum(load.Minutes) ? kept : null;
    }
}
