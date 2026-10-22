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
    IReadOnlyList<Creative>? Creatives, string? BannerUrl = null);

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
    EpisodePatterns? Patterns, IReadOnlyList<ShowTag>? Tags);

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
