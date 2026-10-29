using TvTracker.IntegrationEvents;

namespace Analytics.Data;

/// <summary>
/// The readings of the copy that every insight and endpoint must share: which episodes count, when a show is over,
/// which watches were marked in bulk. Each was written out in a dozen places before, with drift (the worker counted
/// specials in a show's run while the API didn't; half the code read "ended" as Ended only, half as Ended or Cancelled).
/// </summary>
public static class CopyRules
{
    /// <summary>A mark of this many of a show's episodes at one moment says when they were marked, not watched.</summary>
    public const int BulkMark = 8;

    /// <summary>Aired regular episodes: specials (season 0) left out, as Tracking's progress counts.</summary>
    public static IQueryable<EpisodeCopy> AiredRegular(this IQueryable<EpisodeCopy> episodes, DateTimeOffset now) =>
        episodes.Where(e => e.SeasonNumber > 0 && e.AirsAtUtc != null && e.AirsAtUtc <= now);

    /// <summary>The show is over, whatever the provider calls it: TVmaze says Ended, the mock provider also Cancelled.</summary>
    public static bool HasEnded(this ShowCopy show) => show.Status is ShowStatus.Ended or ShowStatus.Cancelled;

    /// <summary>The (show, moment) pairs of a user's bulk marks: <see cref="BulkMark"/> or more episodes of one show at one time.</summary>
    public static HashSet<(Guid ShowId, DateTime WatchedAtUtc)> BulkMarks(IEnumerable<UserEpisodeWatch> watches) =>
        watches.GroupBy(w => (w.ShowId, w.WatchedAtUtc)).Where(g => g.Count() >= BulkMark).Select(g => g.Key).ToHashSet();
}
