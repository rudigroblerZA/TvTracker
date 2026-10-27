using TvTracker.UI.Components;
using TvTracker.UI.Models;

namespace TvTracker.UI.Services;

/// <param name="Reason">Why this show is in the plan ("Fits “something light”", "Next up: S02E05").</param>
public record TonightBlock(TonightShow Show, IReadOnlyList<TonightEpisode> Episodes, int Minutes, string Reason);

/// <param name="Mood">The mood planned for, when the fits were available; null otherwise.</param>
public record TonightPlan(IReadOnlyList<TonightBlock> Blocks, int Minutes, int BudgetMinutes, string? Mood)
{
    public bool IsEmpty => Blocks.Count == 0;
}

/// <summary>
/// Fills an evening from the user's queue: which shows, and how many of each one's next episodes, so the total lands
/// close to the time they have.
/// <list type="bullet">
/// <item>
///   <b>Score:</b> each show's score mixes how well it fits the mood (Catalog's fit, 0-1 within the queue) with
///   continuity: a show watched in the last two weeks beats one left for months, and both beat starting something new.
/// </item>
/// <item>
///   <b>Choice:</b> a small knapsack over each show's next episodes, taken in order. It maximises minutes weighted by
///   score², so time goes to the best fits first, and each extra show costs a little, so it doesn't hop between
///   shows for no reason. Every filled minute is worth a little too, so the evening gets filled. "One show" allows
///   one; "Mix it up" up to three, each taking at most three-quarters of the evening (or one episode, if that's longer), so a
///   mix really mixes, unless that cap leaves the evening under three-quarters full.
/// </item>
/// <item>
///   <b>For two:</b> an episode one of the two has already seen is a rewatch for them, so its minutes count for
///   <see cref="RewatchValue"/> of a new one: a level show beats one where somebody sits through reruns.
/// </item>
/// <item>
///   <b>Running over:</b> allowed by a few minutes (an episode rarely fits the budget exactly), never more.
/// </item>
/// </list>
/// </summary>
public static class TonightPlanner
{
    public const int MaxShowsMixed = 3;

    // Only the best-scoring shows are considered; the rest can't beat them anyway.
    private const int Candidates = 12;

    // Value lost per extra show in the plan, in minute × score² units: about five good minutes.
    private const double SwitchCost = 5;

    // Value of a filled minute whatever the show: an evening filled with a weaker fit beats one left half empty.
    private const double FillValue = 0.15;

    private const double MoodWeight = 0.65;

    // A rewatch for one of two people: still an evening together, but worth less than episodes new to both.
    private const double RewatchValue = 0.6;

    // In a mix, the most of the evening one show may take: room for two hour-long episodes in a two-and-a-half-hour
    // evening, but not the whole of it.
    private const double MixShare = 0.75;

    /// <param name="fits">Each show's mood fit (0-1); null when no mood was given or the fits were unavailable.</param>
    /// <param name="mood">The mood as the user would say it, for the reasons ("Fits “Light & funny”").</param>
    /// <param name="excluded">Shows the user swapped out of earlier plans.</param>
    /// <param name="why">The reason a show is in the plan when it isn't the mood ("You're level"); by default, where the
    /// user is in it.</param>
    public static TonightPlan Plan(IReadOnlyList<TonightShow> queue, IReadOnlyDictionary<Guid, double>? fits, string? mood,
        int budgetMinutes, bool oneShow, IReadOnlySet<Guid> excluded, DateTimeOffset now, Func<TonightShow, string>? why = null)
    {
        var plan = Plan(queue, fits, mood, budgetMinutes, oneShow, excluded, now, capShows: !oneShow, why);
        if (!oneShow && plan.Minutes < budgetMinutes * 0.75)
        {
            var uncapped = Plan(queue, fits, mood, budgetMinutes, oneShow, excluded, now, capShows: false, why);
            if (uncapped.Minutes > plan.Minutes)
            {
                return uncapped;
            }
        }
        return plan;
    }

    private static TonightPlan Plan(IReadOnlyList<TonightShow> queue, IReadOnlyDictionary<Guid, double>? fits, string? mood,
        int budgetMinutes, bool oneShow, IReadOnlySet<Guid> excluded, DateTimeOffset now, bool capShows, Func<TonightShow, string>? why)
    {
        var tolerance = Math.Max(5, budgetMinutes / 12);
        var limit = budgetMinutes + tolerance;
        var maxShows = oneShow ? 1 : MaxShowsMixed;

        var scored = queue
            .Where(s => !excluded.Contains(s.ShowId) && s.Episodes.Count > 0)
            .Select(s => (Show: s, Score: Score(s, fits, now)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(Candidates)
            .ToList();

        // best[used shows, minutes] = (value, choices); choices[i] = how many of show i's episodes.
        var best = new (double Value, int[] Take)?[maxShows + 1, limit + 1];
        best[0, 0] = (0, new int[scored.Count]);
        for (var i = 0; i < scored.Count; i++)
        {
            var (show, score) = scored[i];
            var cap = capShows ? Math.Max((int)(budgetMinutes * MixShare), show.Episodes[0].RuntimeMinutes) : limit;
            var next = ((double Value, int[] Take)?[,])best.Clone();
            for (var used = 0; used < maxShows; used++)
            {
                for (var minutes = 0; minutes <= limit; minutes++)
                {
                    if (best[used, minutes] is not { } state)
                    {
                        continue;
                    }

                    var total = minutes;
                    var showMinutes = 0;
                    var value = state.Value - (used > 0 ? SwitchCost : 0);
                    for (var k = 1; k <= show.Episodes.Count; k++)
                    {
                        total += show.Episodes[k - 1].RuntimeMinutes;
                        showMinutes += show.Episodes[k - 1].RuntimeMinutes;
                        if (total > limit || showMinutes > cap)
                        {
                            break;
                        }
                        value += show.Episodes[k - 1].RuntimeMinutes * (score * score + FillValue)
                            * (show.Episodes[k - 1].SeenBy is null ? 1 : RewatchValue);
                        if (next[used + 1, total] is not { } existing || value > existing.Value)
                        {
                            var take = (int[])state.Take.Clone();
                            take[i] = k;
                            next[used + 1, total] = (value, take);
                        }
                    }
                }
            }
            best = next;
        }

        // The best plan of any size and length; ties go to the one closest to the budget.
        (double Value, int[] Take)? chosen = null;
        var chosenMinutes = 0;
        for (var used = 1; used <= maxShows; used++)
        {
            for (var minutes = 1; minutes <= limit; minutes++)
            {
                if (best[used, minutes] is { } state
                    && (chosen is null || state.Value > chosen.Value.Value + 1e-9
                        || Math.Abs(state.Value - chosen.Value.Value) < 1e-9 && Math.Abs(minutes - budgetMinutes) < Math.Abs(chosenMinutes - budgetMinutes)))
                {
                    (chosen, chosenMinutes) = (state, minutes);
                }
            }
        }

        if (chosen is null)
        {
            return new TonightPlan([], 0, budgetMinutes, fits is null ? null : mood);
        }

        var blocks = scored
            .Select((x, i) => (x.Show, x.Score, Count: chosen.Value.Take[i]))
            .Where(x => x.Count > 0)
            .Select(x =>
            {
                var episodes = x.Show.Episodes.Take(x.Count).ToList();
                return new TonightBlock(x.Show, episodes, episodes.Sum(e => e.RuntimeMinutes), Reason(x.Show, fits, mood, now, why));
            })
            .ToList();
        return new TonightPlan(blocks, blocks.Sum(b => b.Minutes), budgetMinutes, fits is null ? null : mood);
    }

    private static double Score(TonightShow show, IReadOnlyDictionary<Guid, double>? fits, DateTimeOffset now)
    {
        var continuity = Continuity(show, now);
        if (fits is null)
        {
            return continuity;
        }

        // Not embedded yet: no opinion on the mood, so a middling fit rather than none.
        var fit = fits.TryGetValue(show.ShowId, out var f) ? f : 0.5;
        return MoodWeight * fit + (1 - MoodWeight) * continuity;
    }

    /// <summary>1 for a show watched in the last two weeks, down to 0.3 for one only on the watchlist.</summary>
    private static double Continuity(TonightShow show, DateTimeOffset now)
    {
        if (show.Status == WatchStatus.PlanToWatch || show.LastWatchedAtUtc is null)
        {
            return 0.3;
        }

        var days = (now - new DateTimeOffset(DateTime.SpecifyKind(show.LastWatchedAtUtc.Value, DateTimeKind.Utc))).TotalDays;
        return days <= 14 ? 1 : days <= 60 ? 0.7 : 0.45;
    }

    private static string Reason(TonightShow show, IReadOnlyDictionary<Guid, double>? fits, string? mood, DateTimeOffset now,
        Func<TonightShow, string>? why)
    {
        var first = show.Episodes[0];
        var start = first.SeasonNumber == 1 && first.EpisodeNumber == 1 ? "Start from the first episode" : $"Pick up at {Format.EpisodeCode(first.SeasonNumber, first.EpisodeNumber)}";
        var because = fits is not null && fits.TryGetValue(show.ShowId, out var fit) && fit >= 0.7
            ? $"Fits “{mood}”"
            : why is not null
                ? why(show)
                : show.Status == WatchStatus.PlanToWatch || show.LastWatchedAtUtc is null
                    ? "On your watchlist"
                    : Continuity(show, now) >= 1 ? "You're in the middle of it" : "You haven't watched it in a while";
        return $"{because} · {start}";
    }
}
