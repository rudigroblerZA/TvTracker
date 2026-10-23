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
/// <param name="Tags">Short labels for how the show is released and watched (see <see cref="ShowTags"/>); null before they were added.</param>
/// <param name="Dna">The show's DNA; null before it was added.</param>
/// <param name="Outlook">Forecasts for a running show; null for ended shows and before they were added.</param>
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
    EpisodePatterns? Patterns = null,
    IReadOnlyList<ShowTag>? Tags = null,
    ShowDna? Dna = null,
    Outlook? Outlook = null,
    GetsGood? GetsGood = null,
    Landing? Landing = null);

/// <summary>
/// Whether an ended show sticks the landing, from the provider's ratings of its finale (its last aired episode) and
/// final season against the rest. Spoiler-free: no titles. Null for shows still running, or without enough ratings.
/// </summary>
/// <param name="Verdict">One of <see cref="LandingVerdicts"/>.</param>
/// <param name="Seasons">Regular seasons it ran.</param>
/// <param name="FinaleRating">The finale's provider rating.</param>
/// <param name="ShowMedian">The median rating of its episodes.</param>
/// <param name="FinaleBeats">The share of its other episodes the finale rates above, 0-1.</param>
/// <param name="FinalSeasonRank">Where the final season ranks among its rated seasons (1 = best); null with fewer than three.</param>
/// <param name="RankedSeasons">How many seasons were ranked.</param>
public record Landing(string Verdict, int Seasons, double FinaleRating, double ShowMedian, double FinaleBeats,
    int? FinalSeasonRank, int RankedSeasons);

public static class LandingVerdicts
{
    /// <summary>A strong finale, and a final season that holds up.</summary>
    public const string Sticks = "sticks";

    /// <summary>A strong finale after one of its weakest seasons (The Office).</summary>
    public const string GreatFinale = "great-finale";

    /// <summary>The final season holds up, but the finale rates well below the show (Seinfeld).</summary>
    public const string FinaleLetsDown = "finale-lets-down";

    /// <summary>A weak finale at the end of one of its weakest seasons (Game of Thrones, Dexter).</summary>
    public const string Fizzles = "fizzles";

    /// <summary>Finale and final season rate about as the rest did (Breaking Bad, Friends).</summary>
    public const string AsItRan = "as-it-ran";
}

/// <summary>
/// When a show gets good, from the provider's episode ratings over its opening run. Null when the ratings say nothing
/// clear: too few, a mild start, or no lasting turn.
/// </summary>
/// <param name="FromTheStart">Its first episodes already rate as well as the rest of its opening run.</param>
/// <param name="Opening">Its first three rated episodes' average.</param>
/// <param name="Settled">The median of its opening run (its first 40 rated episodes): the level it settles at.</param>
/// <param name="Turn">The episode where it reaches that level and stays there; null when it's good from the start.</param>
public record GetsGood(bool FromTheStart, double Opening, double Settled, GetsGoodTurn? Turn);

/// <param name="EpisodesBefore">Aired regular episodes before it.</param>
/// <param name="MinutesBefore">Their runtime: how long it takes to get there.</param>
/// <param name="Before">The average rating of the rated episodes before it.</param>
/// <param name="After">The average rating of it and the next nine rated episodes.</param>
public record GetsGoodTurn(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, int EpisodesBefore, int MinutesBefore,
    double Before, double After);

/// <summary>
/// What's likely next for a running show, from models trained and backtested on the catalog's own season history each
/// run. Each part is null when the show doesn't qualify (ended, too little history, or the answer is already known).
/// </summary>
public record Outlook(RatingForecast? NextSeasonRating, RenewalOutlook? Renewal, PremiereForecast? NextSeason);

/// <summary>The next season's average episode rating on the provider.</summary>
/// <param name="Low">With <paramref name="High"/>, the 80% range: in backtests, 8 in 10 forecasts like it landed inside.</param>
/// <param name="Backtested">How many past seasons the model was checked against.</param>
public record RatingForecast(int SeasonNumber, double Rating, double Low, double High, int Backtested);

/// <summary>How often comparable shows came back after the season this show is on: a weak signal, stated as such.</summary>
/// <param name="Share">Comparable shows that got another season, 0-1.</param>
/// <param name="Comparable">How many seasons of comparable shows it's based on.</param>
/// <param name="Basis">What made them comparable, e.g. "rated 8-8.5 after their second season, since 2020".</param>
/// <param name="Overall">The share for every show in the catalog, for comparison.</param>
public record RenewalOutlook(int AfterSeason, double Share, int Comparable, string Basis, double Overall);

/// <summary>When the next season is likely to start, from the show's own waits between seasons.</summary>
/// <param name="Earliest">With <paramref name="Latest"/>, the 80% range from backtests.</param>
/// <param name="Overdue">Already past <paramref name="Latest"/>: a longer wait than 9 in 10 comparable ones.</param>
public record PremiereForecast(int SeasonNumber, DateOnly Likely, DateOnly Earliest, DateOnly Latest, bool Overdue, int Backtested);

/// <summary>
/// The show's DNA: traits of its form, rhythm and quality (see <see cref="TvTracker.IntegrationEvents.DnaTraits"/>),
/// in that list's order. Traits without enough data are left out.
/// </summary>
public record ShowDna(IReadOnlyList<DnaTrait> Traits);

/// <param name="Value">0-1, between the trait's two ends.</param>
/// <param name="Label">The end it leans to ("Hour-long episodes"); null in the middle.</param>
/// <param name="Detail">The fact behind it, e.g. "About 47 min an episode".</param>
public record DnaTrait(string Key, double Value, string? Label, string Detail);

/// <param name="Key">One of <see cref="ShowTags"/>.</param>
/// <param name="Detail">The facts behind it, e.g. "8 episodes a season · all 34 in 33 h".</param>
public record ShowTag(string Key, string Label, string Detail);

/// <summary>The tags Analytics gives shows, and the rules behind them.</summary>
public static class ShowTags
{
    /// <summary>
    /// Most recent full season released on a single day, or in up to <see cref="MaxDropDays"/> batches (a streaming-style drop).
    /// Weekly releases aren't tagged: nearly every show is weekly.
    /// </summary>
    public const string DropsAtOnce = "drops-at-once";

    /// <summary>
    /// Short seasons (at most <see cref="BingeableMaxEpisodesPerSeason"/> episodes on average), at most
    /// <see cref="BingeableMaxMinutes"/> in all, with nothing left to air.
    /// </summary>
    public const string Bingeable = "bingeable";

    /// <summary>Everything aired fits in <see cref="WeekendBingeMaxMinutes"/>, with nothing left to air.</summary>
    public const string WeekendBinge = "weekend-binge";

    /// <summary>Our users average at least <see cref="FanFavoriteMinStars"/> (from the minimum number of ratings), or the provider rates it at least <see cref="FanFavoriteMinExternal"/>.</summary>
    public const string FanFavorite = "fan-favorite";

    /// <summary>One season, and the show has ended.</summary>
    public const string LimitedSeries = "limited-series";

    /// <summary>Typical episode at most <see cref="QuickEpisodeMaxMinutes"/> minutes.</summary>
    public const string QuickEpisodes = "quick-episodes";

    /// <summary>At least <see cref="LongRunningMinSeasons"/> seasons.</summary>
    public const string LongRunning = "long-running";

    /// <summary>
    /// Made by someone credited behind the camera on at least <see cref="ProlificMinShows"/> shows on the catalog
    /// provider (Dick Wolf, Chuck Lorre). Labelled "Prolific creator" when they created this show, else "Prolific producer".
    /// </summary>
    public const string ProlificCreator = "prolific-creator";

    public const int MaxDropDays = 3;
    public const int BingeableMaxEpisodesPerSeason = 10;
    public const int BingeableMaxMinutes = 50 * 60;
    public const int WeekendBingeMaxMinutes = 12 * 60;
    public const double FanFavoriteMinStars = 4.3;
    public const double FanFavoriteMinExternal = 8.5;
    public const int QuickEpisodeMaxMinutes = 30;
    public const int LongRunningMinSeasons = 10;

    /// <summary>Same threshold as the catalog's "prolific" on show and person pages.</summary>
    public const int ProlificMinShows = 10;
}

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

    /// <summary>Shows tagged <see cref="ShowTags.Bingeable"/>, best rated first.</summary>
    public const string Bingeable = "bingeable";

    /// <summary>Shows tagged <see cref="ShowTags.WeekendBinge"/>, best rated first.</summary>
    public const string WeekendBinges = "weekend-binges";

    /// <summary>Shows tagged <see cref="ShowTags.FanFavorite"/>: our users' favourites first, then the provider's.</summary>
    public const string FanFavorites = "fan-favorites";

    /// <summary>Well-rated slow starters (<see cref="GetsGoodTurn"/>) that get much better, most popular first.</summary>
    public const string WorthTheWait = "worth-the-wait";

    /// <summary>Well-rated ended shows that stick the landing (<see cref="LandingVerdicts.Sticks"/>), most popular first.</summary>
    public const string GreatEndings = "great-endings";

    public static readonly IReadOnlyList<string> All =
        [HiddenGems, MostConsistent, Rollercoasters, BestFinales, Bingeable, WeekendBinges, FanFavorites, WorthTheWait, GreatEndings];
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

/// <summary>
/// A creator's style, from the DNA of the shows they created or developed: the traits those shows share (their
/// signature), where the shows differ most (their range), shows by other people made the same way, and the creators
/// whose style is closest.
/// </summary>
/// <param name="Shows">Their shows with DNA, most popular first.</param>
/// <param name="Traits">Each trait averaged over their shows (only traits at least two of them have): the fingerprint itself.</param>
/// <param name="Signature">Distinctive traits most of their shows share, strongest first.</param>
/// <param name="Range">Traits where their shows sit at opposite ends, e.g. "Short episodes to hour-long episodes".</param>
public record CreatorFingerprint(
    Guid PersonId,
    string Name,
    DateTime ComputedAtUtc,
    IReadOnlyList<FingerprintShow> Shows,
    IReadOnlyDictionary<string, double> Traits,
    IReadOnlyList<SignatureTrait> Signature,
    IReadOnlyList<string> Range,
    IReadOnlyList<FingerprintMatch> MadeThisWay,
    IReadOnlyList<KindredCreator> Kindred);

public record FingerprintShow(Guid ShowId, string Title, string? PosterUrl);

/// <param name="Shows">How many of their shows lean this way, of <paramref name="Of"/> with the trait.</param>
public record SignatureTrait(string Key, string Label, int Shows, int Of);

/// <param name="SharedTraits">Their signature traits the show has too.</param>
public record FingerprintMatch(Guid ShowId, string Title, string? PosterUrl, double Similarity, IReadOnlyList<string> SharedTraits);

/// <param name="SharedTraits">Signature traits both creators have.</param>
/// <param name="ShowTitles">Their shows, most popular first.</param>
public record KindredCreator(Guid PersonId, string Name, double Similarity, IReadOnlyList<string> SharedTraits, IReadOnlyList<string> ShowTitles);

/// <param name="PersonId">The catalog's id for the co-star, for linking to their page.</param>
public record CoStar(Guid PersonId, string Name, IReadOnlyList<PersonShow> SharedShows);
