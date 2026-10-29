namespace TvTracker.UI.Models;

// Client-side copies of the API contracts. Enums are serialized as numbers,
// so their order must match the services' enums exactly.

public enum ShowStatus { Upcoming, Airing, Renewed, Ended, Cancelled }

public enum WatchStatus { Watching, PlanToWatch, Completed, Paused, Stopped }

public enum EpisodeLabel { None, SeriesPremiere, SeasonPremiere, SeasonFinale }

public enum WatchProviderType { Stream, Free, Rent, Buy }

public enum NotificationKind { NewEpisode, NewSeason, StatusChange, SeasonVerdict, Crossover }

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
/// <param name="SeenBy">Planning for two: "you" or "them" when one of the two has seen it already; null when new to both.</param>
public record TonightEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, int RuntimeMinutes, bool RuntimeEstimated,
    string? SeenBy = null);

public record MoodResponse(bool Available, IReadOnlyList<MoodFit> Fits);

public record MoodFit(Guid ShowId, double Fit, double Distance);

/// <summary>A crossover event across a universe's shows (see Catalog.API's CrossoverApi); parts in air order.</summary>
public record Crossover(Guid Id, string Name, Guid UniverseId, string UniverseName, DateTimeOffset StartsAtUtc, IReadOnlyList<CrossoverPart> Parts);

public record CrossoverPart(Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber, int EpisodeNumber,
    string Title, DateTimeOffset AirsAtUtc);

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
/// <param name="TrailShows">Shows where the actor plays this character, this one included (Catalog.API's CharacterApi).</param>
public record CastMember(string Name, string? Character, string? PhotoUrl, Guid PersonId, int TrailShows = 0);

/// <summary>Every show where an actor plays the same character, by premiere (see Catalog.API's CharacterApi).</summary>
public record CharacterTrail(Guid PersonId, string PersonName, string? PhotoUrl, string Character, IReadOnlyList<TrailShow> Shows);

/// <param name="ShowId">Null for a show outside the catalog.</param>
public record TrailShow(Guid? ShowId, string? ExternalId, string Title, string? PosterUrl, int? Year, ShowStatus Status,
    string Character, bool Voice, double? Rating, DateTimeOffset? FirstAiredUtc, DateTimeOffset? LastAiredUtc, int? Episodes);

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

/// <param name="Url">The show's page on the service (on a phone the OS opens it in the service's app), else a search of
/// the service for it, else the service's site.</param>
/// <param name="Link">What <paramref name="Url"/> opens; null without one.</param>
public record WatchProvider(string ProviderName, WatchProviderType Type, string? Url, WatchLink? Link = null);

/// <summary>What a where-to-watch link opens (Catalog.Data's <c>WatchLink</c>, same order).</summary>
public enum WatchLink { Site, Search, Title }

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

public record ListSummary(Guid Id, string Name, string? Description, int ItemCount, DateTime UpdatedAtUtc, bool Shared = false);

public record ListItem(Guid ShowId, string? Title, string? PosterUrl, int Position, DateTime AddedAtUtc);

/// <param name="Shared">Shown on the owner's Social page to the people who can see them.</param>
public record ListDetail(Guid Id, string Name, string? Description, IReadOnlyList<ListItem> Items, DateTime UpdatedAtUtc, bool Shared = false);

// Ratings

public record MyRating(int Stars, DateTime RatedAtUtc);

public record RatingSummary(double Average, int Count, IReadOnlyList<int> Distribution);

/// <param name="HiddenReason">Only on the user's own hidden comment: "flags", "held" or "moderator".</param>
public record Comment(Guid Id, string DisplayName, string? Text, bool IsSpoiler, bool IsMine, DateTime CreatedAtUtc,
    string? HiddenReason = null, bool FlaggedByMe = false);

public record CommentsPage(bool HiddenUntilWatched, int TotalCount, IReadOnlyList<Comment> Comments);

public record ReactionCount(string Emoji, int Count);

public record Reactions(IReadOnlyList<ReactionCount> Counts, string? Mine);

// Recommendations

public record RecommendedShow(Guid ShowId, string Title, string? PosterUrl, double Score, string Reason);

/// <summary>A show made like another but about something else: the distinctive traits they share, and its other genres.</summary>
public record DnaTwin(Guid ShowId, string Title, string? PosterUrl, double Similarity, List<string> SharedTraits, List<string> OtherGenres);

// Notifications

public record NotificationPreferences(bool NewEpisodes, bool NewSeasons, bool StatusChanges, bool PushEnabled, bool WebPushEnabled,
    bool EmailEnabled, string? Email, TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd, string TimeZone,
    bool WeeklyDigest = false, DayOfWeek DigestDay = DayOfWeek.Sunday, int DigestHour = 9);

public record UpdateNotificationPreferences(bool NewEpisodes, bool NewSeasons, bool StatusChanges, bool PushEnabled, bool WebPushEnabled,
    bool EmailEnabled, TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd,
    bool? WeeklyDigest = null, DayOfWeek? DigestDay = null, int? DigestHour = null);

/// <summary>The weekly digest as it would go out now (Notifications.API).</summary>
public record DigestPreview(string Subject, IReadOnlyList<DigestSection> Sections);

public record DigestSection(string Heading, IReadOnlyList<DigestLine> Lines);

public record DigestLine(string Text, string? Link);

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
    Landing? Landing = null, SeasonVerdict? SeasonVerdict = null);

/// <summary>How a season that just ended did (3 to 45 days after its finale); spoiler-free.</summary>
/// <param name="Verdict">One of <see cref="TvTracker.IntegrationEvents.SeasonVerdicts"/>.</param>
/// <param name="Finale">One of <see cref="TvTracker.IntegrationEvents.FinaleVerdicts"/>; null without a finale rating.</param>
/// <param name="Started">Our users who watched any of it; null under 5. Finished: of those, who watched the finale.</param>
public record SeasonVerdict(int Number, DateTime FinaleAiredAtUtc, double Rating, string Verdict, double? OthersMedian, string? Finale,
    int? Started, int? Finished);

public record SeasonVerdictItem(Guid ShowId, string Title, SeasonVerdict Verdict);

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
/// <param name="Noted">Episodes moderators marked essential or skippable, which the cut follows before ratings.</param>
public record EssentialCut(Guid ShowId, string Title, string Level, bool Available, string? Reason, CutTotals Full, CutTotals Cut,
    IReadOnlyList<CutSeason> Seasons, IReadOnlyList<SkipRun> Skips, CutProgress? Progress, int Noted = 0);

public record CutTotals(int Episodes, int Minutes);

public record CutSeason(int Number, int Episodes, int Kept, int MinutesSaved, IReadOnlyList<CutEpisode> Items);

/// <param name="Why">"series-premiere", "season-premiere", "season-finale", "series-finale", "two-parter",
/// "best-of-season", "above-average", "unrated", "below-average" (skipped), "noted-essential" or "noted-skippable".</param>
/// <param name="Note">A moderator's reason, for a noted episode, with its source.</param>
public record CutEpisode(Guid EpisodeId, int EpisodeNumber, string Title, double? Rating, bool Keep, string Why, bool Watched,
    string? Note = null, string? NoteSource = null);

/// <param name="Noted">Skipped by moderators' notes rather than ratings.</param>
public record SkipRun(int SeasonNumber, int FromEpisode, int ToEpisode, int Episodes, int Minutes, double? AverageRating, double SeasonAverage,
    bool Noted = false);

public record CutProgress(Guid? NextEpisodeId, int? NextSeason, int? NextEpisode, CutTotals FullRemaining, CutTotals CutRemaining);

/// <summary>Taste twins (Analytics.API): anonymous people whose ratings track the user's, and what they loved.</summary>
public record TasteTwins(int RatedShows, int Twins, double? Agreement, string? Reason, IReadOnlyList<TwinShow> AgreeOn, IReadOnlyList<TwinPick> Picks);

public record TwinShow(Guid ShowId, string Title, string? PosterUrl);

/// <param name="Loved">How many twins rated it 4 or 5 stars.</param>
public record TwinPick(Guid ShowId, string Title, string? PosterUrl, int Loved, double Stars, double? Rating);

/// <param name="Wins">Award wins on Wikidata; null when only saved research has them.</param>
/// <param name="Checked">From saved research, checked by a moderator.</param>
public record AwardedShow(Guid ShowId, string Title, string? PosterUrl, int? Wins, string Summary, bool Checked, string? SourceUrl);

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

/// <summary>Someone on the app, as Social.API describes them to the caller.</summary>
/// <param name="Requested">The user asked to follow this private profile and is waiting for approval.</param>
/// <param name="BlockedByYou">On the user's block list.</param>
public record SocialPerson(string UserId, string DisplayName, int Followers, int Following, bool IsFollowing, bool FollowsYou, bool IsYou,
    bool Private = false, bool Requested = false, bool BlockedByYou = false);

/// <param name="Requests">Follow requests waiting for the user's approval.</param>
public record MySocial(string UserId, string DisplayName, bool ShareActivity, int Followers, int Following, bool Private = false, int Requests = 0);

/// <summary>Leave either out (null) to keep it as it is.</summary>
public record SocialSettings(bool? ShareActivity, bool? Private = null);

/// <param name="Shares">Whether they share their activity; when not, <paramref name="Activity"/> is empty.</param>
/// <param name="CanSee">False for a private profile the user isn't an approved follower of: only the name shows.</param>
public record SocialPersonPage(SocialPerson Person, bool Shares, IReadOnlyList<FeedItem> Activity, bool CanSee = true,
    IReadOnlyList<SharedListSummary>? Lists = null);

public record FollowState(bool Following, bool Requested);

public record SharedListSummary(Guid ListId, string Name, string? Description, int Shows, DateTime UpdatedAtUtc);

public record SharedList(Guid ListId, SocialPerson Owner, string Name, string? Description, IReadOnlyList<ListShow> Shows, DateTime UpdatedAtUtc);

public record ListShow(Guid ShowId, string? Title, string? PosterUrl);

/// <summary>How far someone is through a show (regular episodes).</summary>
public record PeerProgress(int Watched, int Aired, EpisodeCode? Furthest, bool Finished);

public record FriendOnShow(string UserId, string DisplayName, bool Following, PeerProgress Progress, int? Stars);

public record FriendsOnShow(int Total, IReadOnlyList<FriendOnShow> People);

/// <param name="Ahead">"you", "them", "level", or null when neither has started.</param>
public record TogetherShow(Guid ShowId, string? Title, string? PosterUrl, PeerProgress You, PeerProgress Them, string? Ahead);

public record Together(SocialPerson Person, IReadOnlyList<TogetherShow> Shows);

/// <summary>Tonight for two: what the user and someone they follow could watch together (see Social.API's TonightTogetherApi).</summary>
public record TonightTogether(SocialPerson Person, IReadOnlyList<TonightTogetherShow> Shows, IReadOnlyList<ApartShow> Apart);

/// <param name="Ahead">"you", "them", "level", or null when neither has started.</param>
/// <param name="Gap">How far apart the two are, in episodes.</param>
public record TonightTogetherShow(Guid ShowId, string Title, string? PosterUrl, string? Ahead, int Gap, DateTime? LastWatchedAtUtc,
    IReadOnlyList<TogetherEpisode> Episodes);

/// <param name="SeenBy">"you" or "them" when one has seen it already; null when new to both.</param>
public record TogetherEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, int RuntimeMinutes, bool RuntimeEstimated, string? SeenBy);

/// <summary>A shared show one of the two is too far ahead in to watch together tonight.</summary>
public record ApartShow(Guid ShowId, string Title, string Ahead, int Gap);

public record Feed(IReadOnlyList<FeedItem> Items, DateTime? NextBefore);

/// <param name="Kind">"watched", "finished", "followed" or "rated".</param>
public record FeedItem(Guid Id, string UserId, string DisplayName, string Kind, Guid ShowId, string? ShowTitle, string? PosterUrl,
    int Episodes, EpisodeCode? First, EpisodeCode? Last, int? Stars, DateTime OccurredAtUtc);

public record EpisodeCode(int SeasonNumber, int EpisodeNumber);

/// <summary>A feature flag on the flags page (see Identity.API's FlagsApi).</summary>
/// <param name="Default">What flagd.json says.</param>
/// <param name="Targeting">flagd.json treats some users differently; an override applies to everyone.</param>
/// <param name="Override">Set on the flags page; null when flagd.json decides.</param>
public record FeatureFlagSetting(string Key, string? Description, bool Default, bool Targeting, bool? Override, bool On);

/// <summary>The result of flagging a comment or community insight (see Ratings.API's CommunityApi).</summary>
public record FlagResult(bool Hidden, bool MarkedSpoiler);

/// <summary>A viewer's tip about a show or an episode, ranked by how many found it helpful.</summary>
/// <param name="HiddenReason">Only on the user's own hidden insight.</param>
public record CommunityInsight(Guid Id, Guid ShowId, Guid? EpisodeId, string DisplayName, string Text, bool IsSpoiler, int Helpful,
    bool VotedHelpful, bool IsMine, bool FlaggedByMe, string? HiddenReason, DateTime CreatedAtUtc);

public record CommunityInsights(int Total, IReadOnlyList<CommunityInsight> Insights);

/// <summary>A suggested fix to show or episode data. Status: pending, accepted, rejected or reverted.</summary>
public record Correction(Guid Id, Guid ShowId, Guid? EpisodeId, string Field, string? CurrentValue, string SuggestedValue,
    string? Note, string? Context, string DisplayName, string Status, string? ReviewedBy, DateTime? ReviewedAtUtc, DateTime CreatedAtUtc);

public record PostFlag(string Reason, string? Note, DateTime CreatedAtUtc);

/// <param name="Kind">"comment" or "insight".</param>
public record ModeratedPost(string Kind, Guid Id, Guid ShowId, Guid? EpisodeId, string Author, string Text, bool IsSpoiler,
    string? HiddenReason, DateTime CreatedAtUtc, IReadOnlyList<PostFlag> Flags);

public record ModerationLogItem(string Moderator, string Action, string TargetKind, Guid TargetId, string? Summary, DateTime AtUtc);

public record ModerationQueue(IReadOnlyList<ModeratedPost> Posts, IReadOnlyList<Correction> Corrections, IReadOnlyList<Correction> Accepted,
    IReadOnlyList<ModerationLogItem> Log);

/// <summary>"Where do I know them from?" for a show's cast (see Analytics.API's KnownFacesApi).</summary>
public record KnownFaces(Guid ShowId, int CastCount, IReadOnlyList<KnownFace> People);

public record KnownFace(Guid PersonId, string Name, string? Character, IReadOnlyList<KnownFrom> From);

/// <param name="CaughtUp">False when the user is partway through that show, so the actor may only arrive later.</param>
public record KnownFrom(Guid ShowId, string Title, string? PosterUrl, string? Character, int Watched, int Aired, bool CaughtUp);

/// <summary>A holiday with episodes made for it (see Catalog.API's HolidayApi).</summary>
public record HolidaySummary(string Key, string Name, DateOnly Date, int DaysAway);

public record Holiday(string Key, string Name, DateOnly Date, int DaysAway, IReadOnlyList<HolidayEpisode> Episodes,
    IReadOnlyList<HolidayShow> SeasonShows);

/// <param name="StandsAlone">A comedy, animated, reality or game show, whose episodes usually work out of order.</param>
public record HolidayEpisode(Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber, int EpisodeNumber,
    string Title, DateTimeOffset? AirsAtUtc, int? RuntimeMinutes, double? Rating, bool InTitle, bool StandsAlone);

public record HolidayShow(Guid ShowId, string Title, string? PosterUrl);

/// <summary>Research workspace for moderators (see Research.API's ResearchApi).</summary>
public record ResearchWorkspace(Guid ShowId, string Title, DateTime? GatheredAtUtc, string? Draft, DateTime? DraftingSinceUtc, string? DraftProblem,
    IReadOnlyList<ResearchSourceItem> Sources, IReadOnlyList<ResearchFact> Facts, SavedResearch? Saved, IReadOnlyList<ResearchVersion> Versions,
    IReadOnlyList<string> Notes, string Model, bool TmdbConfigured);

/// <param name="Number">How drafts and the insight cite it, while it's included.</param>
/// <param name="Mark">"essential" or "skippable".</param>
public record EpisodeNote(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Mark, string Reason, string SourceUrl,
    DateTime NotedAtUtc, string NotedBy);

/// <summary>What to research next (Research.API's queue).</summary>
public record ResearchQueue(IReadOnlyList<QueuedShow> Unresearched, IReadOnlyList<DisagreeingShow> Disagreements, DateTime? CheckedAtUtc);

/// <param name="Rank">Its place among the catalog's most popular.</param>
/// <param name="Started">Gathered or drafted, not saved.</param>
public record QueuedShow(Guid ShowId, string Title, string? PosterUrl, int Rank, string? Network, int? Year, bool Started);

public record DisagreeingShow(Guid ShowId, string Title, int Rank, bool Researched, string WikidataUrl, IReadOnlyList<Disagreement> Fields);

/// <param name="Field">"network", "premiered" or "episodes".</param>
public record Disagreement(string Field, string? Catalog, string Wikidata);

public record ResearchSourceItem(Guid Id, int? Number, string Kind, string Url, string Title, string Excerpt, bool Included, DateTime RetrievedAtUtc);

/// <param name="Origin">"saved", "catalog" or "source".</param>
public record ResearchFactOption(string Value, string Origin, Guid? SourceId, string? SourceTitle);

/// <param name="CatalogField">Set when saving this fact overrides the catalog's (TVmaze's) value.</param>
public record ResearchFact(string Key, string Label, string Kind, string? CatalogField, string? Saved, string? Catalog, IReadOnlyList<ResearchFactOption> Options);

public record ResearchVersion(int Version, DateTime SavedAtUtc, string SavedBy);

public record SavedResearch(Guid ShowId, int Version, string Insight, IReadOnlyList<SavedResearchFact> Facts, IReadOnlyList<SavedResearchSource> Sources,
    DateTime SavedAtUtc, string SavedBy);

/// <param name="Source">The number of the source it came from, as the insight cites it; null when typed by hand.</param>
public record SavedResearchFact(string Key, string Label, string Kind, string Value, int? Source);

public record SavedResearchSource(int Number, Guid Id, string Kind, string Title, string Url, DateTime RetrievedAtUtc);

public record ResearchedShow(Guid ShowId, string Title, int? Version, DateTime? SavedAtUtc, DateTime UpdatedAtUtc);

public record ResearchFactChoice(string Key, string Value, Guid? SourceId);

public record ResearchSaveResult(SavedResearch Saved, IReadOnlyList<ResearchCatalogOutcome> Catalog);

/// <param name="Outcome">"applied" or "removed".</param>
public record ResearchCatalogOutcome(string Key, string Field, string Outcome);

// Ask the app (Assistant.API)

public record AssistantAbout(bool Available, string Model, int PerHour);

public record AssistantStep(string Tool, string Arguments, bool Failed);

public record AssistantShow(Guid Id, string Title);

/// <param name="Status">"queued", "thinking", "answered" or "failed".</param>
public record AssistantAsk(Guid Id, string Question, Guid? PreviousId, string Status, int Ahead, List<AssistantStep> Steps, string? Answer,
    List<AssistantShow> Shows, string? Problem, DateTime AskedAtUtc, double? Seconds);

/// <summary>Network report cards: every network with enough catalog shows, and the whole catalog to compare with.</summary>
public record NetworkReportCards(NetworkReport? Baseline, IReadOnlyList<NetworkReport> Networks);

public record NetworkReport(string Network, int Shows, int Ended, double? AverageRating, NetworkEndings? Endings,
    NetworkRenewals? Renewals, int? MedianWaitDays, int Waits, double DropsAtOnce, IReadOnlyList<NetworkShow> Best,
    IReadOnlyList<NetworkShow> GreatEndings);

public record NetworkEndings(int Judged, int Sticks, int AsItRan, int Weaker, int Fizzles);

public record NetworkRenewals(int Decided, int Renewed);

public record NetworkShow(Guid ShowId, string Title, string? PosterUrl, double? Rating);

/// <summary>A year of the user's viewing (Year in TV).</summary>
public record YearInTv(int Year, IReadOnlyList<int> Years, int Episodes, int Minutes, int Shows, int NewShows, int Bulk,
    int DaysWatched, int LongestStreak, YearDay? BusiestDay, YearMonth? BusiestMonth, IReadOnlyList<YearShow> TopShows,
    IReadOnlyList<YearShare> Genres, YearShare? TopNetwork, IReadOnlyList<YearShow> Finished, IReadOnlyList<YearShow> CaughtUp,
    IReadOnlyList<YearShow> LeftBehind, YearEpisode? BestEpisode, IReadOnlyList<YearShift> Shift, YearCalledIt? CalledIt);

public record YearShow(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes);

public record YearDay(DateOnly Date, int Episodes, int Minutes);

public record YearMonth(int Month, string Name, int Episodes, int Minutes);

public record YearShare(string Name, int Minutes, double Share);

public record YearEpisode(Guid ShowId, string ShowTitle, string? PosterUrl, int SeasonNumber, int EpisodeNumber, string Title, double Rating);

public record YearShift(string Key, string Name, double From, double To, string Toward);

public record YearCalledIt(Guid ShowId, string Title, string? PosterUrl, DateTime FollowedAtUtc, double Ahead, int Followers, double? Rating);
