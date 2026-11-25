namespace TvTracker.InsightsEngine;

/// <param name="Fits">Finished shows that fit the time on their own, best first.</param>
/// <param name="Mix">Two or three of them that fill the time better than any one, when there are.</param>
public record TimeOffDto(int BudgetMinutes, IReadOnlyList<TimeOffShowDto> Fits, TimeOffMixDto? Mix);

/// <param name="Value">How much it's likely worth the user's time (see ShowValue), on the rating's 0-10 scale.</param>
/// <param name="Landing">How it ends, when known (a <see cref="LandingVerdicts"/> verdict).</param>
public record TimeOffShowDto(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes, double Value, string? Landing,
    bool Limited, IReadOnlyList<string> Reasons);

public record TimeOffMixDto(IReadOnlyList<TimeOffShowDto> Shows, int Minutes);

/// <summary>
/// "Fits your time off": finished shows the user can watch from start to end in the time they have (a holiday, a long
/// weekend), ranked by how much they're likely worth it to them. Only ended shows: the point is an ending. Shows the user
/// has started are left out, as are shows that fizzle out at the end. Computed on request.
/// </summary>
public static class TimeOff
{
    /// <summary>
    /// A single fit fills at least this share of the time, so a 20-hour holiday isn't offered a 2-hour show.
    /// </summary>
    private const double MinFill = 0.25;

    /// <summary>
    /// Candidates for a mix, best first.
    /// </summary>
    private const int MixCandidates = 30;

    /// <summary>
    /// A mix replaces nothing unless it fills this share of the time.
    /// </summary>
    private const double MinMixFill = 0.6;

    private const int MaxFits = 10;

    /// <summary>
    /// Shows worth less than this aren't suggested at all.
    /// </summary>
    private const double MinValue = 7.3;

    /// <summary>
    /// Shorter than this isn't a show to fill time off with.
    /// </summary>
    private const int MinMinutes = 60;

    /// <summary>
    /// A mix's score is Σ minutes × (value − this): a show at this value adds nothing by being longer.
    /// </summary>
    private const double MixValueFloor = 6.5;

    /// <summary>
    /// The finished shows worth offering the viewer, which Fits your time off and While you wait share: on the shelf, not
    /// started, long enough to count, and not ending in a fizzle.
    /// </summary>
    internal static List<ShelfCandidate> Candidates(IEnumerable<ShelfShow> shelf, IReadOnlySet<Guid> started, HashSet<string> liked) =>
        shelf.Where(s => !started.Contains(s.ShowId) && s.Minutes >= MinMinutes)
            .Select(s => new ShelfCandidate(s, ShowValue.Assess(s.ExternalRating, s.Genres, s.Landing, s.Turn, s.Limited, liked)))
            .Where(c => c.Assessment.Landing is not LandingVerdicts.Fizzles)
            .ToList();

    /// <summary>
    /// What fits a budget of minutes: the single shows, best first, and the best mix of two or three.
    /// </summary>
    internal static TimeOffDto Of(IEnumerable<ShelfCandidate> candidates, int budget)
    {
        var overrun = ViewerRules.Overrun(budget);
        var worth = candidates
            .Where(c => c.Show.Minutes <= budget + overrun && c.Assessment.Value >= MinValue)
            .Select(c => new TimeOffShowDto(c.Show.ShowId, c.Show.Title, c.Show.PosterUrl, c.Show.Episodes, c.Show.Minutes, c.Assessment.Value,
                c.Assessment.Landing, c.Assessment.Limited, c.Assessment.Reasons))
            .OrderByDescending(c => c.Value)
            .ThenByDescending(c => c.Minutes)
            .ToList();

        var fits = worth.Where(c => c.Minutes >= budget * MinFill).Take(MaxFits).ToList();
        return new TimeOffDto(budget, fits, Mix(worth.Take(MixCandidates).ToList(), budget + overrun, fits));
    }

    /// <summary>
    /// The best two or three shows together, by minutes × (value − <see cref="MixValueFloor"/>): a fuller evening of good shows beats a short one
    /// of great shows, as in Tonight's plan. Only offered when it fills more of the time than the best single fit.
    /// </summary>
    private static TimeOffMixDto? Mix(List<TimeOffShowDto> candidates, int limit, List<TimeOffShowDto> fits)
    {
        double Score(IEnumerable<TimeOffShowDto> shows) => shows.Sum(s => s.Minutes * (s.Value - MixValueFloor));

        List<TimeOffShowDto>? best = null;
        void Consider(List<TimeOffShowDto> mix)
        {
            if (mix.Sum(s => s.Minutes) <= limit && (best is null || Score(mix) > Score(best)))
            {
                best = mix;
            }
        }

        for (var a = 0; a < candidates.Count; a++)
        {
            for (var b = a + 1; b < candidates.Count; b++)
            {
                Consider([candidates[a], candidates[b]]);
                for (var c = b + 1; c < candidates.Count; c++)
                {
                    Consider([candidates[a], candidates[b], candidates[c]]);
                }
            }
        }

        var minutes = best?.Sum(s => s.Minutes) ?? 0;
        var bestSingle = fits.Select(f => f.Minutes).DefaultIfEmpty(0).Max();
        return best is not null && minutes >= limit * MinMixFill && minutes > bestSingle
            ? new TimeOffMixDto(best.OrderByDescending(s => s.Value).ToList(), minutes)
            : null;
    }
}
