namespace TvTracker.ShowInsightsEngine;

/// <summary>
/// A show's next season premiere: a scheduled first episode or an announced season, whichever is sooner.
/// </summary>
/// <param name="NewSeason">A season premiere (or an announced season), rather than the next episode of a season already airing.</param>
public sealed record NextPremiere(int SeasonNumber, DateTimeOffset PremieresAtUtc, bool NewSeason);

/// <summary>
/// Everything a viewer insight needs about one show for one viewer: the show, its regular episodes in season
/// order, the viewer's watches and stars, the ratings the essential cut goes by, moderators' notes and the stored
/// document. Loaded for a set of shows in eight queries whatever their number, so the list endpoints (every dropped
/// show, every refresher due, every catch-up) stop running five or six queries per show.
/// </summary>
public sealed class ShowLoad(ShowCopy show, List<EpisodeCopy> episodes, List<UserEpisodeWatch> watches, Dictionary<Guid, int> stars,
    Func<EpisodeCopy, double?> rating, Dictionary<Guid, EpisodeNoteCopy> notes, ShowInsights? insights, DateTimeOffset now)
{
    public ShowCopy Show => show;

    /// <summary>
    /// Regular episodes, aired or not, in season order.
    /// </summary>
    public List<EpisodeCopy> Episodes => episodes;

    public List<EpisodeCopy> Aired { get; } = episodes.Where(e => e.AirsAtUtc <= now).ToList();

    public List<UserEpisodeWatch> Watches => watches;

    public HashSet<Guid> Watched { get; } = watches.Select(w => w.EpisodeId).ToHashSet();

    /// <summary>
    /// The viewer's own stars per episode.
    /// </summary>
    public Dictionary<Guid, int> Stars => stars;

    /// <summary>
    /// The essential cut's rating of an episode (the provider's, averaged with our users' once enough rated it; see <see cref="EssentialCut.Rating"/>).
    /// </summary>
    public Func<EpisodeCopy, double?> Rating => rating;

    public Dictionary<Guid, EpisodeNoteCopy> Notes => notes;

    public ShowInsights? Insights => insights;

    /// <summary>
    /// Episode runtimes, with missing ones as the show's typical one (see <see cref="EssentialCut.MinutesOf"/>).
    /// </summary>
    public Func<EpisodeCopy, int> Minutes => field ??= EssentialCut.MinutesOf(Aired);
}
