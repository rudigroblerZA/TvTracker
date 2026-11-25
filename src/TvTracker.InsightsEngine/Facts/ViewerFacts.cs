namespace TvTracker.InsightsEngine;

/// <summary>
/// A show's next season premiere: a scheduled first episode or an announced season, whichever is sooner.
/// </summary>
/// <param name="NewSeason">A season premiere (or an announced season), rather than the next episode of a season already airing.</param>
public sealed record NextPremiere(int SeasonNumber, DateTimeOffset PremieresAtUtc, bool NewSeason)
{
    /// <summary>
    /// The next thing to air for a show: a scheduled season premiere or the soonest announced season, else (when
    /// <paramref name="premieresOnly"/> is false) the next episode of the season under way. Null with nothing scheduled.
    /// </summary>
    /// <param name="episodes">The show's listed episodes.</param>
    /// <param name="announced">Its soonest announced season still to come.</param>
    public static NextPremiere? Of(IEnumerable<EpisodeCopy> episodes, AnnouncedSeason? announced, DateTimeOffset now, bool premieresOnly)
    {
        var scheduled = episodes.Where(e => e.AirsAtUtc > now && (!premieresOnly || e.EpisodeNumber == 1)).MinBy(e => e.AirsAtUtc);
        return (scheduled, announced) switch
        {
            ({ } e, { } s) when s.PremieresAtUtc < e.AirsAtUtc => new NextPremiere(s.SeasonNumber, s.PremieresAtUtc!.Value, true),
            ({ } e, _) => new NextPremiere(e.SeasonNumber, e.AirsAtUtc!.Value, e.EpisodeNumber == 1),
            (null, { } s) => new NextPremiere(s.SeasonNumber, s.PremieresAtUtc!.Value, true),
            _ => null
        };
    }
}

/// <summary>
/// Everything a viewer insight needs about one show for one viewer: the show, its regular episodes in season
/// order, the viewer's watches and stars, the ratings the essential cut goes by, moderators' notes and the stored
/// document.
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

/// <summary>
/// A viewer's progress on one show, as one row of counts. Regular episodes only, as Tracking counts.
/// </summary>
/// <param name="Aired">Aired regular episodes.</param>
/// <param name="Watched">Of those, how many the viewer has watched.</param>
/// <param name="LatestSeason">The latest season with an aired episode.</param>
/// <param name="LatestWatched">The latest aired episode (by season and number) is watched: caught up, allowing skipped episodes.</param>
/// <param name="MinutesLeft">The running time of the unwatched aired episodes (missing runtimes as 0).</param>
/// <param name="LastAiredAtUtc">The latest aired episode's air time.</param>
/// <param name="LastWatchedAtUtc">The viewer's latest watch of the show; null when they've watched none of it.</param>
public sealed record ShowProgress(Guid ShowId, int Aired, int Watched, int LatestSeason, bool LatestWatched, int MinutesLeft,
    DateTimeOffset? LastAiredAtUtc, DateTime? LastWatchedAtUtc)
{
    public bool AllWatched => Aired > 0 && Watched >= Aired;
    public bool Started => Watched > 0;
    public int Left => Aired - Watched;
}
