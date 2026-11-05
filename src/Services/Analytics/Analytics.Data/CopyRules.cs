using TvTracker.IntegrationEvents;

namespace Analytics.Data;

/// <summary>
/// The readings of the copy that every insight and endpoint must share: which episodes count, when a show is over,
/// which watches were marked in bulk. Each was written out in a dozen places before, with drift (the worker counted
/// specials in a show's run while the API didn't; half the code read "ended" as Ended only, half as Ended or Cancelled).
/// </summary>
public static class CopyRules
{
    /// <summary>
    /// A mark of this many of a show's episodes at one moment says when they were marked, not watched.
    /// </summary>
    public const int BulkMark = 8;

    /// <summary>
    /// Aired regular episodes: specials (season 0) left out, as Tracking's progress counts.
    /// </summary>
    public static IQueryable<EpisodeCopy> AiredRegular(this IQueryable<EpisodeCopy> episodes, DateTimeOffset now) =>
        episodes.Where(e => e.SeasonNumber > 0 && e.AirsAtUtc != null && e.AirsAtUtc <= now);

    /// <summary>
    /// The show is over, whatever the provider calls it: TVmaze says Ended, the mock provider also Cancelled.
    /// </summary>
    public static bool HasEnded(this ShowCopy show) => show.Status is ShowStatus.Ended or ShowStatus.Cancelled;

    /// <summary>
    /// <see cref="HasEnded"/> in a query: the shows that are over, and those that aren't.
    /// </summary>
    public static IQueryable<ShowCopy> Ended(this IQueryable<ShowCopy> shows) =>
        shows.Where(s => s.Status == ShowStatus.Ended || s.Status == ShowStatus.Cancelled);

    public static IQueryable<ShowCopy> Running(this IQueryable<ShowCopy> shows) =>
        shows.Where(s => s.Status != ShowStatus.Ended && s.Status != ShowStatus.Cancelled);

    /// <summary>
    /// The (show, moment) pairs of a user's bulk marks: <see cref="BulkMark"/> or more episodes of one show at one time.
    /// </summary>
    public static HashSet<(Guid ShowId, DateTime WatchedAtUtc)> BulkMarks(IEnumerable<UserEpisodeWatch> watches) =>
        BulkMarks(watches, w => w.ShowId, w => w.WatchedAtUtc);

    /// <summary>
    /// The same, for watch rows projected with their show and time.
    /// </summary>
    public static HashSet<(Guid ShowId, DateTime WatchedAtUtc)> BulkMarks<T>(IEnumerable<T> watches, Func<T, Guid> show, Func<T, DateTime> at) =>
        watches.GroupBy(w => (show(w), at(w))).Where(g => g.Count() >= BulkMark).Select(g => g.Key).ToHashSet();
}
