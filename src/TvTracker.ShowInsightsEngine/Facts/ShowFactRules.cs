using TvTracker.IntegrationEvents;

namespace TvTracker.ShowInsightsEngine;

/// <summary>
/// Rules about the facts themselves that every insight shares: when a show counts as over, and when watches were
/// marked in bulk. The same rules as queries, for the database, are in Analytics.Data's CopyRules.
/// </summary>
public static class ShowFactRules
{
    /// <summary>
    /// A mark of this many of a show's episodes at one moment says when they were marked, not watched.
    /// </summary>
    public const int BulkMark = 8;

    /// <summary>
    /// The show is over, whatever the provider calls it: TVmaze says Ended, the mock provider also Cancelled.
    /// </summary>
    public static bool HasEnded(this ShowCopy show) => show.Status is ShowStatus.Ended or ShowStatus.Cancelled;

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
