namespace Analytics.Data;

/// <summary>
/// Everything Analytics knows about one show, computed hourly and served as-is by Analytics.API.
/// Our users' averages appear only once at least <see cref="InsightRules.MinRatingsToShow"/> ratings back them.
/// </summary>
/// <param name="Trend">"rising", "falling" or "steady" across seasons; null with fewer than three rated seasons.</param>
/// <param name="Histograms">Rating distributions; null in documents computed before they were added.</param>
/// <param name="Viewing">Our users' viewing; null below the minimum number of viewers.</param>
/// <param name="Momentum">Our users' follows; null below the minimum number of followers.</param>
/// <param name="Patterns">How the show's episode ratings behave, from the provider's ratings; null before they were added.</param>
public record ShowInsights(
    Guid ShowId,
    string Title,
    DateTime ComputedAtUtc,
    RatingPair Rating,
    IReadOnlyList<SeasonStats> Seasons,
    SeasonHighlight? BestSeason,
    SeasonHighlight? WorstSeason,
    string? Trend,
    IReadOnlyList<TopEpisode> TopEpisodes,
    RunFacts Run,
    Premiere? NextPremiere,
    IReadOnlyList<SharedCast> SharedCast,
    Histograms? Histograms = null,
    Viewing? Viewing = null,
    Momentum? Momentum = null,
    EpisodePatterns? Patterns = null);

/// <param name="Finales">Season finales against their season's average; null with no finished, rated season.</param>
/// <param name="SeasonBests">The best-rated episode of each season with rated episodes.</param>
/// <param name="Consistency">How much episode ratings vary; null below the minimum number of rated episodes.</param>
/// <param name="Arc">Where the show peaked and whether it declined after; null with fewer than three rated seasons.</param>
public record EpisodePatterns(FinaleStats? Finales, IReadOnlyList<SeasonBest> SeasonBests, Consistency? Consistency, Arc? Arc);

/// <param name="Seasons">Finished seasons with enough rated episodes to compare.</param>
/// <param name="BeatSeasonAverage">Of those, how many finales rated above their season's average.</param>
/// <param name="AverageDifference">Finale rating minus season average, averaged over those seasons.</param>
public record FinaleStats(int Seasons, int BeatSeasonAverage, double AverageDifference);

public record SeasonBest(int SeasonNumber, Guid EpisodeId, int EpisodeNumber, string Title, double Rating);

/// <param name="Spread">Standard deviation of aired episodes' ratings (0-10 scale).</param>
/// <param name="Label">"steady", "varied" or "rollercoaster" (see <see cref="InsightRules"/>).</param>
public record Consistency(double Spread, string Label);

/// <param name="DeclineFromSeason">
/// The first season after the peak from which every rated season stayed well below it; null when the show held up.
/// </param>
/// <param name="PeakIsLatest">True when the latest rated season is the best one.</param>
public record Arc(int PeakSeason, double PeakRating, int? DeclineFromSeason, bool PeakIsLatest);

/// <param name="OurStars">How many of our users gave the show 1-5 stars (index 0 is 1 star); null below the minimum.</param>
/// <param name="ExternalEpisodes">Aired episodes by provider rating, in half-point buckets from the lowest to the highest.</param>
public record Histograms(IReadOnlyList<int>? OurStars, IReadOnlyList<HistogramBucket> ExternalEpisodes);

/// <param name="From">The bucket's lower bound, e.g. 7.5 for ratings from 7.5 up to 8.</param>
public record HistogramBucket(double From, int Count);

/// <param name="Viewers">Our users who watched at least one episode.</param>
/// <param name="CompletionRate">Share of viewers (0-1) who have watched every aired episode.</param>
/// <param name="Seasons">Viewers per season, for seasons with at least the minimum number.</param>
/// <param name="DropOff">Where the most viewers stopped; null when no episode has the minimum number of them.</param>
public record Viewing(int Viewers, double? CompletionRate, IReadOnlyList<SeasonViewers> Seasons, DropOff? DropOff);

public record SeasonViewers(int Number, int Viewers);

/// <summary>
/// The episode after which the most viewers stopped: their furthest episode, with nothing watched on the show for
/// <see cref="InsightRules.StoppedAfterDays"/> days (so people mid-binge don't count) and aired episodes still left.
/// </summary>
/// <param name="Share">Stopped viewers as a share (0-1) of all viewers.</param>
public record DropOff(int SeasonNumber, int EpisodeNumber, string Title, int Stopped, double Share);

/// <param name="Followers">Our users following the show now.</param>
/// <param name="NewFollowsByWeek">Follows started in each of the last eight weeks, oldest first.</param>
/// <param name="RecentFollows">Follows started in the last four weeks.</param>
/// <param name="PreviousFollows">Follows started in the four weeks before that.</param>
public record Momentum(int Followers, IReadOnlyList<int> NewFollowsByWeek, int RecentFollows, int PreviousFollows);

/// <param name="External">The catalog provider's rating (0-10), e.g. TVmaze's.</param>
/// <param name="Ours">Our users' average stars (1-5); null below the minimum number of ratings.</param>
/// <param name="OurCount">How many of our users rated it.</param>
public record RatingPair(double? External, double? Ours, int OurCount);

public record SeasonStats(
    int Number,
    int EpisodeCount,
    int AiredCount,
    DateTimeOffset? FirstAirsAtUtc,
    DateTimeOffset? LastAirsAtUtc,
    int RuntimeMinutes,
    RatingPair Rating);

/// <param name="Source">"external" when ranked by the provider's ratings, "ours" when by our users'.</param>
public record SeasonHighlight(int Number, double Rating, string Source);

public record TopEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, DateTimeOffset? AirsAtUtc, RatingPair Rating);

/// <param name="Seasons">Seasons with at least one aired episode.</param>
public record RunFacts(
    int Seasons,
    int AiredEpisodes,
    int RuntimeMinutes,
    DateTimeOffset? FirstAiredUtc,
    DateTimeOffset? LastAiredUtc,
    SeasonBreak? LongestBreak);

/// <summary>The gap between the last episode of one season and the first of the next.</summary>
public record SeasonBreak(int AfterSeason, int BeforeSeason, int Days);

/// <param name="DaysSincePreviousSeason">Days from the previous season's last episode to the premiere.</param>
/// <param name="CatchUpEpisodes">Episodes aired before the premiere: what to watch to be ready.</param>
public record Premiere(
    int SeasonNumber,
    DateTimeOffset PremieresAtUtc,
    int? DaysSincePreviousSeason,
    int CatchUpEpisodes,
    int CatchUpRuntimeMinutes);

/// <summary>An actor (or character) this show shares with another show in the catalog.</summary>
/// <param name="SameCharacter">True when the provider lists the same character in both, not just the same actor.</param>
/// <param name="PersonId">The catalog's id for the person, for linking to their page; null when not known yet.</param>
public record SharedCast(
    string PersonName,
    string? CharacterHere,
    Guid OtherShowId,
    string OtherShowTitle,
    string? OtherShowPosterUrl,
    string? CharacterThere,
    bool SameCharacter,
    Guid? PersonId = null);

public static class InsightRules
{
    /// <summary>Our users' averages are hidden until this many ratings back them, so no one's rating can be singled out.</summary>
    public const int MinRatingsToShow = 5;

    /// <summary>Viewing and follow figures need this many users behind them, for the same reason.</summary>
    public const int MinUsersToShow = 5;

    /// <summary>A viewer counts as having stopped after this many days without watching the show.</summary>
    public const int StoppedAfterDays = 30;

    /// <summary>
    /// Episode-rating spreads (standard deviation) below this are "steady", from <see cref="RollercoasterSpread"/>
    /// a "rollercoaster", "varied" between. On TVmaze, most shows fall between 0.25 and 0.6 (median about 0.4).
    /// </summary>
    public const double SteadySpread = 0.3;

    public const double RollercoasterSpread = 0.6;

    /// <summary>Consistency needs this many rated episodes.</summary>
    public const int MinRatedEpisodesForConsistency = 20;

    /// <summary>A season counts as a decline from the peak when it's at least this far below it (0-10 scale).</summary>
    public const double DeclineMargin = 0.3;
}

/// <summary>The discovery lists Analytics keeps, by their API names.</summary>
public static class DiscoveryLists
{
    /// <summary>Highly rated shows that aren't among the most popular, and that few of our users follow yet.</summary>
    public const string HiddenGems = "hidden-gems";

    /// <summary>Well-rated shows whose episodes vary least.</summary>
    public const string MostConsistent = "most-consistent";

    /// <summary>Well-rated shows whose episodes vary most.</summary>
    public const string Rollercoasters = "rollercoasters";

    /// <summary>Season finales, ranked like top episodes.</summary>
    public const string BestFinales = "best-finales";

    public static readonly IReadOnlyList<string> All = [HiddenGems, MostConsistent, Rollercoasters, BestFinales];
}

/// <summary>Everything Analytics knows about one actor, from the catalog shows they're credited in.</summary>
/// <param name="AverageShowRating">The provider's ratings of their shows, averaged; null when none are rated.</param>
/// <param name="FirstYear">The year the earliest of their shows first aired.</param>
/// <param name="LastYear">The year the latest episode of their shows aired.</param>
/// <param name="FrequentCoStars">Actors credited in at least two of the same shows, most shared first.</param>
/// <param name="Followers">Our users following at least one of their shows; null below the minimum number.</param>
public record PersonInsights(
    Guid PersonId,
    string Name,
    DateTime ComputedAtUtc,
    int ShowCount,
    double? AverageShowRating,
    PersonShow? BestShow,
    int? FirstYear,
    int? LastYear,
    IReadOnlyList<CoStar> FrequentCoStars,
    int? Followers);

public record PersonShow(Guid ShowId, string Title, string? PosterUrl, double? Rating, string? Character);

/// <param name="PersonId">The catalog's id for the co-star, for linking to their page.</param>
public record CoStar(Guid PersonId, string Name, IReadOnlyList<PersonShow> SharedShows);
