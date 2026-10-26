namespace TvTracker.UI.Models;

// Client-side copies of the API contracts. Enums are serialized as numbers,
// so their order must match the services' enums exactly.

public enum ShowStatus { Upcoming, Airing, Renewed, Ended, Cancelled }

public enum WatchStatus { Watching, PlanToWatch, Completed, Paused, Stopped }

public enum EpisodeLabel { None, SeriesPremiere, SeasonPremiere, SeasonFinale }

public enum WatchProviderType { Stream, Free, Rent, Buy }

public enum NotificationKind { NewEpisode, NewSeason, StatusChange }

public enum DevicePlatform { Android, iOS, Web }

// Identity

public record Profile(string UserId, string DisplayName, string? Email, string? AvatarUrl, string TimeZone, string Region, string Language);

public record UpdateProfile(string DisplayName, string? AvatarUrl, string TimeZone, string Region, string Language);

// Catalog

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public record ShowSummary(Guid Id, string Title, string? PosterUrl, int? Year, ShowStatus Status, string? Network, IReadOnlyList<string> Genres);

/// <summary>Shows linked by shared characters (see Catalog.API's Universes).</summary>
/// <param name="ToYear">The year of its last episode; null while any of its shows is still running.</param>
public record UniverseSummary(Guid Id, string Name, int ShowCount, int CharacterCount, int? FromYear, int? ToYear, IReadOnlyList<ShowSummary> Posters);

/// <param name="Shows">Oldest first.</param>
public record Universe(Guid Id, string Name, IReadOnlyList<UniverseShow> Shows, IReadOnlyList<UniverseLink> Links);

public record UniverseShow(ShowSummary Show, int Characters, IReadOnlyList<TimelineSeason>? Seasons = null);

/// <param name="Aired">Episodes aired by now; the rest are scheduled.</param>
public record TimelineSeason(int Number, DateTimeOffset From, DateTimeOffset To, int Episodes, int Aired);

/// <param name="A">The earlier show.</param>
/// <param name="Kind">"revival", "continuation", "crossover" or "franchise" (no shared characters: the same franchise name).</param>
public record UniverseLink(Guid A, Guid B, string Kind, IReadOnlyList<SharedCharacter> Characters);

public record SharedCharacter(Guid PersonId, string PersonName, string? PhotoUrl, string Character);

/// <summary>A show in the user's queue for Tonight's plan (see Tracking.API's TonightApi): its next unwatched episodes.</summary>
public record TonightShow(Guid ShowId, string Title, string? PosterUrl, IReadOnlyList<string> Genres, WatchStatus Status,
    DateTime? LastWatchedAtUtc, int WatchedCount, IReadOnlyList<TonightEpisode> Episodes);

/// <param name="RuntimeEstimated">The provider has no runtime; it's the show's typical one.</param>
public record TonightEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, int RuntimeMinutes, bool RuntimeEstimated);

public record MoodResponse(bool Available, IReadOnlyList<MoodFit> Fits);

public record MoodFit(Guid ShowId, double Fit, double Distance);

/// <summary>A universe's episodes in air order, across its shows (see Catalog.API's UniverseApi).</summary>
public record WatchOrder(Guid Id, string Name, IReadOnlyList<ShowSummary> Shows, IReadOnlyList<WatchOrderEpisode> Episodes, int Undated);

/// <param name="Crossover">The crossover event it's part of ("Infection"), when the shows' titles say so.</param>
public record WatchOrderEpisode(Guid Id, Guid ShowId, int SeasonNumber, int EpisodeNumber, string Title, DateTimeOffset AirsAtUtc,
    int? RuntimeMinutes, string? Crossover);

/// <summary>Shows between two others (see Catalog.API's BlendApi).</summary>
public record Blend(ShowSummary A, ShowSummary B, int Lean, bool Indexed, IReadOnlyList<BlendMatch> Matches, bool Loose);

/// <param name="LikeA">Where the show sits between the two, as a percentage like A.</param>
public record BlendMatch(ShowSummary Show, int Fit, int LikeA, IReadOnlyList<string> GenresFromA, IReadOnlyList<string> GenresFromB,
    IReadOnlyList<string> GenresFromBoth);

public record SeasonSummary(int Number, string Name, int EpisodeCount, DateTimeOffset? PremieresAtUtc);

/// <param name="PersonId">Empty in responses cached before people had pages (e.g. offline on mobile); don't link then.</param>
public record CastMember(string Name, string? Character, string? PhotoUrl, Guid PersonId);

public record PersonCredit(ShowSummary Show, string? Character);

/// <param name="Career">Their career behind the camera (from TVmaze); null when not known.</param>
/// <param name="Made">Catalog shows they created or executive produced.</param>
public record PersonDetail(Guid Id, string Name, string? PhotoUrl, DateOnly? Birthday, DateOnly? Deathday, string? Gender, string? Country,
    IReadOnlyList<PersonCredit> Credits, Career? Career, IReadOnlyList<MadeCredit>? Made);

public record MadeCredit(ShowSummary Show, IReadOnlyList<string> Roles);

/// <summary>Someone who made a show: a creator, developer or executive producer.</summary>
public record Creative(Guid PersonId, string Name, string? PhotoUrl, IReadOnlyList<string> Roles, Career? Career, IReadOnlyList<ShowRef> OtherShows)
{
    public bool IsCreator => Roles.Contains("Creator") || Roles.Contains("Developer");
}

/// <param name="Shows">Shows they're credited on behind the camera, across TVmaze.</param>
/// <param name="Prolific">Credited on at least 10 shows.</param>
public record Career(int Shows, int Created, int ExecutiveProduced, bool Prolific, IReadOnlyList<string> NotableShows);

public record ShowRef(Guid Id, string Title);

public record WatchProvider(string ProviderName, WatchProviderType Type, string? Url);

public record Episode(Guid Id, Guid ShowId, int SeasonNumber, int EpisodeNumber, string Title, string? Overview,
    DateTimeOffset? AirsAtUtc, int? RuntimeMinutes, EpisodeLabel Label);

public record ShowDetail(Guid Id, string Title, string? Overview, string? PosterUrl, string? BackdropUrl, DateOnly? FirstAirDate,
    ShowStatus Status, string? Network, IReadOnlyList<string> Genres, IReadOnlyList<SeasonSummary> Seasons,
    IReadOnlyList<CastMember> Cast, string Region, IReadOnlyList<WatchProvider> WatchProviders, Episode? NextEpisode, Episode? LastEpisode,
    IReadOnlyList<Creative>? Creatives, string? BannerUrl = null, IReadOnlyList<NanogenreRef>? Nanogenres = null);

/// <summary>A fine-grained genre the catalog discovered ("Sports comedy"), by its slug for /nanogenres/{slug}.</summary>
public record NanogenreRef(string Slug, string Name);

/// <param name="Posters">Posters of its most popular shows.</param>
public record NanogenreSummary(string Slug, string Name, int ShowCount, IReadOnlyList<string> Posters);

public record NanogenreDetail(string Slug, string Name, string Source, IReadOnlyList<ShowSummary> Shows);

public record SeasonDetail(Guid ShowId, int Number, string Name, string? Overview, DateTimeOffset? PremieresAtUtc, IReadOnlyList<Episode> Episodes);

// Tracking

public record Follow(Guid ShowId, string? Title, string? PosterUrl, ShowStatus? ShowStatus, WatchStatus Status,
    int WatchedCount, int AiredCount, DateTime FollowedAtUtc);

public record MarkWatched(IReadOnlyList<Guid>? EpisodeIds, Guid? ShowId, int? SeasonNumber, Guid? UpToEpisodeId, DateTimeOffset? WatchedAtUtc);

public record EpisodeRef(Guid EpisodeId, Guid ShowId, int SeasonNumber, int EpisodeNumber, string Title, DateTimeOffset? AirsAtUtc, int? RuntimeMinutes);

public record UpNextItem(Guid ShowId, string ShowTitle, string? PosterUrl, EpisodeRef Episode, bool HasAired, int RemainingAired, int WatchedCount, int AiredCount);

public record CalendarItem(Guid ShowId, string ShowTitle, string? PosterUrl, EpisodeRef Episode, bool Watched);

public record SeasonProgress(int SeasonNumber, int EpisodeCount, int AiredCount, int WatchedCount);

public record ShowProgress(Guid ShowId, WatchStatus? Status, int EpisodeCount, int AiredCount, int WatchedCount, double PercentComplete,
    EpisodeRef? NextEpisode, IReadOnlyList<SeasonProgress> Seasons, IReadOnlyList<Guid> WatchedEpisodeIds);

public record GenreCount(string Genre, int Episodes);

public record YearStats(int Year, int Episodes, int Minutes, int ShowsStarted);

public record Stats(int EpisodesWatched, int MinutesWatched, int ShowsFollowed, int ShowsCompleted, IReadOnlyList<GenreCount> TopGenres, IReadOnlyList<YearStats> Years);

public record ListSummary(Guid Id, string Name, string? Description, int ItemCount, DateTime UpdatedAtUtc);

public record ListItem(Guid ShowId, string? Title, string? PosterUrl, int Position, DateTime AddedAtUtc);

public record ListDetail(Guid Id, string Name, string? Description, IReadOnlyList<ListItem> Items, DateTime UpdatedAtUtc);

// Ratings

public record MyRating(int Stars, DateTime RatedAtUtc);

public record RatingSummary(double Average, int Count, IReadOnlyList<int> Distribution);

public record Comment(Guid Id, string DisplayName, string? Text, bool IsSpoiler, bool IsMine, DateTime CreatedAtUtc);

public record CommentsPage(bool HiddenUntilWatched, int TotalCount, IReadOnlyList<Comment> Comments);

public record ReactionCount(string Emoji, int Count);

public record Reactions(IReadOnlyList<ReactionCount> Counts, string? Mine);

// Recommendations

public record RecommendedShow(Guid ShowId, string Title, string? PosterUrl, double Score, string Reason);

/// <summary>A show made like another but about something else: the distinctive traits they share, and its other genres.</summary>
public record DnaTwin(Guid ShowId, string Title, string? PosterUrl, double Similarity, List<string> SharedTraits, List<string> OtherGenres);

// Notifications

public record NotificationPreferences(bool NewEpisodes, bool NewSeasons, bool StatusChanges, bool PushEnabled, bool WebPushEnabled,
    bool EmailEnabled, string? Email, TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd, string TimeZone);

public record UpdateNotificationPreferences(bool NewEpisodes, bool NewSeasons, bool StatusChanges, bool PushEnabled, bool WebPushEnabled,
    bool EmailEnabled, TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd);

public record ShowNotificationPreference(Guid ShowId, bool Muted);

public record RegisterDevice(DevicePlatform Platform, string Token, string? P256dh, string? Auth);

public record RegisteredDevice(Guid Id, DevicePlatform Platform, DateTime CreatedAtUtc);

public record InboxItem(Guid Id, NotificationKind Kind, string Title, string Body, string? Link, Guid ShowId, Guid? EpisodeId, DateTime CreatedAtUtc, bool IsRead);

public record InboxPage(IReadOnlyList<InboxItem> Items, int UnreadCount);

// Analytics

/// <param name="External">The catalog provider's rating (0-10, TVmaze's).</param>
/// <param name="Ours">Our users' average stars (1-5); null until enough of them have rated.</param>
public record RatingPair(double? External, double? Ours, int OurCount);

public record SeasonStats(int Number, int EpisodeCount, int AiredCount, DateTimeOffset? FirstAirsAtUtc, DateTimeOffset? LastAirsAtUtc,
    int RuntimeMinutes, RatingPair Rating);

public record SeasonHighlight(int Number, double Rating, string Source);

public record TopEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, DateTimeOffset? AirsAtUtc, RatingPair Rating);

public record SeasonBreak(int AfterSeason, int BeforeSeason, int Days);

public record RunFacts(int Seasons, int AiredEpisodes, int RuntimeMinutes, DateTimeOffset? FirstAiredUtc, DateTimeOffset? LastAiredUtc,
    SeasonBreak? LongestBreak);

public record Premiere(int SeasonNumber, DateTimeOffset PremieresAtUtc, int? DaysSincePreviousSeason, int CatchUpEpisodes, int CatchUpRuntimeMinutes);

/// <param name="PersonId">The actor's page; null in insights computed before it was known.</param>
public record SharedCast(string PersonName, string? CharacterHere, Guid OtherShowId, string OtherShowTitle, string? OtherShowPosterUrl,
    string? CharacterThere, bool SameCharacter, Guid? PersonId);

public record ShowInsights(Guid ShowId, string Title, DateTime ComputedAtUtc, RatingPair Rating, IReadOnlyList<SeasonStats> Seasons,
    SeasonHighlight? BestSeason, SeasonHighlight? WorstSeason, string? Trend, IReadOnlyList<TopEpisode> TopEpisodes, RunFacts Run,
    Premiere? NextPremiere, IReadOnlyList<SharedCast> SharedCast, Histograms? Histograms, Viewing? Viewing, Momentum? Momentum,
    EpisodePatterns? Patterns, IReadOnlyList<ShowTag>? Tags, ShowDna? Dna = null, Outlook? Outlook = null, GetsGood? GetsGood = null,
    Landing? Landing = null);

/// <summary>Whether an ended show sticks the landing; spoiler-free (no titles).</summary>
/// <param name="Verdict">"sticks", "great-finale", "finale-lets-down", "fizzles", "weaker-last-season" or "as-it-ran".</param>
/// <param name="FinalSeasonRank">The final season's rank among RankedSeasons (1 = best); null with fewer than three.</param>
/// <param name="RatedHigher">Other episodes rated above the finale; RatedLower below; ties in neither.</param>
public record Landing(string Verdict, double FinaleRating, double ShowMedian, double FinaleBeats, int? FinalSeasonRank, int RankedSeasons,
    int RatedHigher = 0, int RatedLower = 0, int OtherEpisodes = 0, int SeasonsCompared = 0, int SeasonsHigher = 0, int SeasonsLower = 0);

/// <summary>When a show gets good: from the start, or at <see cref="Turn"/> after a slow start.</summary>
public record GetsGood(bool FromTheStart, double Opening, double Settled, GetsGoodTurn? Turn);

/// <param name="Before">The average rating of the episodes before it; After, of it and the next nine.</param>
public record GetsGoodTurn(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, int EpisodesBefore, int MinutesBefore,
    double Before, double After);

/// <param name="EpisodesAway">Episodes between the user's furthest one and it.</param>
public record GetsGoodAhead(int SeasonNumber, int EpisodeNumber, string Title, int EpisodesAway, int MinutesAway);

/// <summary>Forecasts for a running show; each part is null when the show doesn't qualify.</summary>
public record Outlook(RatingForecast? NextSeasonRating, RenewalOutlook? Renewal, PremiereForecast? NextSeason);

/// <param name="Low">With High, the range 8 in 10 backtested forecasts landed in.</param>
public record RatingForecast(int SeasonNumber, double Rating, double Low, double High, int Backtested);

/// <param name="Share">Comparable shows that came back, 0-1.</param>
/// <param name="Basis">What made them comparable, e.g. "rated 8.0-8.5 after their second season".</param>
/// <param name="Overall">The share for every show in the catalog.</param>
public record RenewalOutlook(int AfterSeason, double Share, int Comparable, string Basis, double Overall);

public record PremiereForecast(int SeasonNumber, DateOnly Likely, DateOnly Earliest, DateOnly Latest, bool Overdue, int Backtested);

/// <param name="Status">"on-track", "slowing" or "stalled".</param>
public record FinishOutlook(Guid ShowId, string Title, string? PosterUrl, int Watched, int Aired, int MinutesLeft, string Status,
    DateTime LastWatchedAtUtc, double EpisodesPerWeek, DateOnly? FinishBy, FinishRecord? Record, DropOff? DropOffAhead,
    GetsGoodAhead? GetsGoodAhead = null);

/// <param name="Comparable">The user's other shows they got at least this far into.</param>
public record FinishRecord(int Comparable, int Finished);

/// <param name="Traits">In <see cref="TvTracker.IntegrationEvents.DnaTraits.All"/> order; traits without enough data are missing.</param>
public record ShowDna(IReadOnlyList<DnaTrait> Traits);

/// <param name="Value">0-1, between the trait's two ends.</param>
/// <param name="Label">The end it leans to; null in the middle.</param>
public record DnaTrait(string Key, double Value, string? Label, string Detail);

/// <param name="Key">e.g. "bingeable", "drops-at-once", "fan-favorite".</param>
public record ShowTag(string Key, string Label, string Detail);

public record EpisodePatterns(FinaleStats? Finales, IReadOnlyList<SeasonBest> SeasonBests, Consistency? Consistency, Arc? Arc);

public record FinaleStats(int Seasons, int BeatSeasonAverage, double AverageDifference);

public record SeasonBest(int SeasonNumber, Guid EpisodeId, int EpisodeNumber, string Title, double Rating);

/// <param name="Label">"steady", "varied" or "rollercoaster".</param>
public record Consistency(double Spread, string Label);

public record Arc(int PeakSeason, double PeakRating, int? DeclineFromSeason, bool PeakIsLatest);

/// <param name="OurStars">Counts of 1-5 star ratings from our users (index 0 is 1 star); null until enough have rated.</param>
public record Histograms(IReadOnlyList<int>? OurStars, IReadOnlyList<HistogramBucket> ExternalEpisodes);

public record HistogramBucket(double From, int Count);

public record Viewing(int Viewers, double? CompletionRate, IReadOnlyList<SeasonViewers> Seasons, DropOff? DropOff);

public record SeasonViewers(int Number, int Viewers);

public record DropOff(int SeasonNumber, int EpisodeNumber, string Title, int Stopped, double Share);

public record Momentum(int Followers, IReadOnlyList<int> NewFollowsByWeek, int RecentFollows, int PreviousFollows);

public record PremiereItem(Guid ShowId, string ShowTitle, string? PosterUrl, IReadOnlyList<string> Genres, int SeasonNumber,
    DateTimeOffset PremieresAtUtc, double? ShowExternalRating, int? DaysSincePreviousSeason, int CatchUpEpisodes, int CatchUpRuntimeMinutes,
    int? RecentFollows);

public record TopEpisodeItem(Guid EpisodeId, Guid ShowId, string ShowTitle, string? PosterUrl, int SeasonNumber, int EpisodeNumber,
    string Title, DateTimeOffset? AirsAtUtc, RatingPair Rating);

/// <param name="Value">What the list ranks by: the show's rating, its episode-rating spread, or the finale's rating.</param>
public record DiscoveryItem(Guid ShowId, string ShowTitle, string? PosterUrl, double? ShowExternalRating, double Value,
    Guid? EpisodeId, int? SeasonNumber, int? EpisodeNumber, string? EpisodeTitle);

/// <summary>A week of TV history: this week in every past year (EveryYear), or one date's week.</summary>
public record TimeMachine(DateOnly Date, bool EveryYear, List<TimeMachineEpisode> Premieres, List<TimeMachineEpisode> Finales,
    List<TimeMachineEpisode> BestEpisodes, List<TimeMachineShow> OnAir);

public record TimeMachineEpisode(Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber, int EpisodeNumber,
    string Title, DateTimeOffset AiredAtUtc, double? Rating, bool Watched);

public record TimeMachineShow(Guid ShowId, string Title, string? PosterUrl, int SeasonNumber);

/// <summary>A creator's style from their shows' DNA; see Analytics' CreatorFingerprint.</summary>
public record CreatorFingerprint(Guid PersonId, string Name, DateTime ComputedAtUtc, List<FingerprintShow> Shows,
    Dictionary<string, double> Traits, List<SignatureTrait> Signature, List<string> Range, List<FingerprintMatch> MadeThisWay,
    List<KindredCreator> Kindred);

public record FingerprintShow(Guid ShowId, string Title, string? PosterUrl);

/// <param name="Shows">How many of their shows lean this way, of Of with the trait.</param>
public record SignatureTrait(string Key, string Label, int Shows, int Of);

public record FingerprintMatch(Guid ShowId, string Title, string? PosterUrl, double Similarity, List<string> SharedTraits);

public record KindredCreator(Guid PersonId, string Name, double Similarity, List<string> SharedTraits, List<string> ShowTitles);

/// <param name="Followers">Our users following at least one of their shows; null until there are enough.</param>
public record PersonInsights(Guid PersonId, string Name, DateTime ComputedAtUtc, int ShowCount, double? AverageShowRating,
    PersonShow? BestShow, int? FirstYear, int? LastYear, IReadOnlyList<CoStar> FrequentCoStars, int? Followers);

public record PersonShow(Guid ShowId, string Title, string? PosterUrl, double? Rating, string? Character);

public record CoStar(Guid PersonId, string Name, IReadOnlyList<PersonShow> SharedShows);

/// <summary>A shorter path through a long show (see Analytics.API's EssentialCutApi).</summary>
public record EssentialCut(Guid ShowId, string Title, string Level, bool Available, string? Reason, CutTotals Full, CutTotals Cut,
    IReadOnlyList<CutSeason> Seasons, IReadOnlyList<SkipRun> Skips, CutProgress? Progress);

public record CutTotals(int Episodes, int Minutes);

public record CutSeason(int Number, int Episodes, int Kept, int MinutesSaved, IReadOnlyList<CutEpisode> Items);

/// <param name="Why">"series-premiere", "season-premiere", "season-finale", "series-finale", "two-parter",
/// "best-of-season", "above-average", "unrated" or "below-average" (skipped).</param>
public record CutEpisode(Guid EpisodeId, int EpisodeNumber, string Title, double? Rating, bool Keep, string Why, bool Watched);

public record SkipRun(int SeasonNumber, int FromEpisode, int ToEpisode, int Episodes, int Minutes, double AverageRating, double SeasonAverage);

public record CutProgress(Guid? NextEpisodeId, int? NextSeason, int? NextEpisode, CutTotals FullRemaining, CutTotals CutRemaining);

public record ReunionRadar(int LovedShows, int ActorsPending, IReadOnlyList<Reunion> Reunions);

/// <param name="IsNew">Premiering soon or in the last year.</param>
public record Reunion(ReunionShow Show, ReunionSource From, IReadOnlyList<ReunionPerson> People, double Score, bool IsNew);

/// <param name="ShowId">Set when the show is in the catalog; otherwise search finds (and imports) it.</param>
public record ReunionShow(string ExternalId, Guid? ShowId, string Title, string? PosterUrl, ShowStatus Status, DateOnly? Premiered, double? Rating);

public record ReunionSource(Guid ShowId, string Title, string? PosterUrl);

public record ReunionPerson(Guid PersonId, string Name, string? CharacterThen, string? CharacterNow, bool Voice);

public record Taste(RatingBias? Bias, IReadOnlyList<GenreTaste> Genres, IReadOnlyList<MonthWatch> Months, TasteProgress Progress);

/// <param name="Label">"kinder", "harsher" or "in line".</param>
public record RatingBias(int Compared, double AverageDifference, string Label, IReadOnlyList<BiasShow> Kinder, IReadOnlyList<BiasShow> Harsher);

public record BiasShow(Guid ShowId, string Title, string? PosterUrl, int Stars, double ExternalRating);

public record GenreTaste(string Genre, int Rated, double AverageStars, double? AverageExternal);

public record MonthWatch(int Year, int Month, int Episodes, int Minutes);

public record TasteProgress(int Finished, int Watching, int Dropped, IReadOnlyList<ProgressShow> RecentlyDropped);

public record ProgressShow(Guid ShowId, string Title, string? PosterUrl, int Watched, int Aired, DateTime LastWatchedAtUtc);

/// <summary>"Previously on…": a rewatch for coming back after a long break (see Analytics.API's RefresherApi).</summary>
public record Refresher(Guid ShowId, string Title, string? PosterUrl, bool Due, DateTime LastWatchedAtUtc, int DaysSince,
    RefresherEpisode LeftOff, RefresherAhead Ahead, RefresherReturn? Returns, int BudgetMinutes, int PlanMinutes,
    IReadOnlyList<RefresherEpisode> Plan);

/// <param name="Why">"left-off", "season-premiere", "before-you-stopped", "season-best", "last-season-finale",
/// "last-season-best" or "two-parter".</param>
public record RefresherEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, int Minutes, double? Rating,
    int? YourStars, string Why);

public record RefresherAhead(int Episodes, int Minutes, Guid? NextEpisodeId, int? NextSeason, int? NextEpisode);

public record RefresherReturn(int SeasonNumber, DateTimeOffset PremieresAtUtc, bool NewSeason);

/// <summary>The catch-up planner: what to watch, and at what pace, before a new season (see Analytics.API's CatchUpApi).</summary>
/// <param name="Path">"full" or "cut": which path <paramref name="Weeks"/> follow.</param>
public record CatchUpPlan(Guid ShowId, string Title, string? PosterUrl, CatchUpPremiere Premiere, int DaysLeft, CatchUpPath Full,
    CatchUpPath? Cut, int YourMinutesPerWeek, string Path, IReadOnlyList<CatchUpWeek> Weeks);

public record CatchUpPremiere(int SeasonNumber, DateTimeOffset PremieresAtUtc);

/// <param name="Verdict">"comfortable", "doable", "tight", "behind", or null without a pace.</param>
public record CatchUpPath(int Episodes, int Minutes, double EpisodesPerWeek, int MinutesPerWeek, string? Verdict, DateOnly? StartBy);

public record CatchUpWeek(DateOnly Starts, int Minutes, IReadOnlyList<CatchUpEpisode> Episodes);

public record CatchUpEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, int Minutes);

/// <summary>"Is it worth going back?" for a dropped show (see Analytics.API's ComebackApi).</summary>
/// <param name="Verdict">"gets-good", "better", "same", "worse" or "unknown".</param>
public record Comeback(Guid ShowId, string Title, string? PosterUrl, int Watched, int Aired, DateTime LastWatchedAtUtc,
    ComebackEpisode StoppedAt, string Verdict, double? BeforeYouStopped, double? Ahead, int AheadEpisodes, int AheadMinutes,
    ComebackFrom? BetterFrom, ComebackTurn? GetsGoodAt, double? YourAverage);

public record ComebackEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title);

public record ComebackFrom(int SeasonNumber, bool RestOfSeason, double Average);

public record ComebackTurn(int SeasonNumber, int EpisodeNumber, int EpisodesAway, int MinutesAway);

/// <summary>Backlog triage (see Analytics.API's BacklogApi).</summary>
public record Backlog(int Minutes, int YourMinutesPerWeek, int? WeeksToClear, int LetGoMinutes, int CutSavesMinutes,
    IReadOnlyList<BacklogShow> Shows, IReadOnlyList<Comeback> Dropped);

/// <param name="Verdict">"start-next", "worth-it", "long", "ends-badly" or "let-it-go".</param>
public record BacklogShow(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes, int? CutMinutes, double Value,
    string Verdict, IReadOnlyList<string> Reasons);

/// <summary>"Fits your time off" (see Analytics.API's TimeOffApi).</summary>
public record TimeOff(int BudgetMinutes, IReadOnlyList<TimeOffShow> Fits, TimeOffMix? Mix);

public record TimeOffShow(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes, double Value, string? Landing,
    bool Limited, IReadOnlyList<string> Reasons);

public record TimeOffMix(IReadOnlyList<TimeOffShow> Shows, int Minutes);
