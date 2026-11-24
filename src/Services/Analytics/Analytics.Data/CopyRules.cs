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
    /// Aired regular episodes: specials (season 0) left out, as Tracking's progress counts.
    /// </summary>
    public static IQueryable<EpisodeCopy> AiredRegular(this IQueryable<EpisodeCopy> episodes, DateTimeOffset now) =>
        episodes.Where(e => e.SeasonNumber > 0 && e.AirsAtUtc != null && e.AirsAtUtc <= now);

    /// <summary>
    /// <see cref="HasEnded"/> in a query: the shows that are over, and those that aren't.
    /// </summary>
    public static IQueryable<ShowCopy> Ended(this IQueryable<ShowCopy> shows) =>
        shows.Where(s => s.Status == ShowStatus.Ended || s.Status == ShowStatus.Cancelled);

    public static IQueryable<ShowCopy> Running(this IQueryable<ShowCopy> shows) =>
        shows.Where(s => s.Status != ShowStatus.Ended && s.Status != ShowStatus.Cancelled);
}
