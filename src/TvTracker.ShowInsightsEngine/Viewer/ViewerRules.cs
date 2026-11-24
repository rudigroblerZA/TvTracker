namespace TvTracker.ShowInsightsEngine;

/// <summary>
/// What the viewer insights share: the viewer's calendar, and where they are in a show.
/// </summary>
public static class ViewerRules
{
    /// <summary>
    /// The viewer's time zone, from the profile's IANA id the client sends (<c>tz</c>); UTC when it's missing or unknown.
    /// </summary>
    public static TimeZoneInfo Zone(string? tz) =>
        tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found) ? found : TimeZoneInfo.Utc;

    /// <summary>
    /// The date <paramref name="at"/> falls on in the viewer's calendar.
    /// </summary>
    public static DateOnly LocalDate(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>
    /// Where the viewer is in a show's aired episodes (in season order): the index of the furthest one watched (-1 for
    /// none) and everything after it. Episodes skipped earlier on don't count as still to watch.
    /// </summary>
    public static (int Furthest, List<EpisodeCopy> Ahead) Split(List<EpisodeCopy> aired, HashSet<Guid> watched)
    {
        var furthest = aired.FindLastIndex(e => watched.Contains(e.EpisodeId));
        return (furthest, aired.Skip(furthest + 1).ToList());
    }

    /// <summary>
    /// A slow start's turn when it's still ahead of the viewer: the episodes between the furthest watched and the
    /// turn, and their running time; null when the turn is behind them or isn't in the list.
    /// </summary>
    public static (GetsGoodTurn Turn, int EpisodesAway, int MinutesAway)? TurnAhead(List<EpisodeCopy> aired, int furthest, GetsGoodTurn? turn,
        Func<EpisodeCopy, int> minutes)
    {
        if (turn is null || aired.FindIndex(e => e.EpisodeId == turn.EpisodeId) is var at && at <= furthest)
        {
            return null;
        }
        var between = aired.Skip(furthest + 1).Take(at - furthest - 1).ToList();
        return (turn, between.Count, between.Sum(minutes));
    }

    /// <summary>
    /// Tonight's plan's overrun, shared by the features that fill a time budget: max(5 min, a twelfth).
    /// </summary>
    public static int Overrun(int budgetMinutes) => Math.Max(5, budgetMinutes / 12);
}
