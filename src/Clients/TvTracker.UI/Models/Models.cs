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
/// <param name="LinkToken">The secret in the list's link; null without one.</param>
public record ListDetail(Guid Id, string Name, string? Description, IReadOnlyList<ListItem> Items, DateTime UpdatedAtUtc, bool Shared = false,
    string? LinkToken = null);

/// <summary>A list as its link shows it to anyone: no owner.</summary>
public record LinkedList(string Name, string? Description, IReadOnlyList<LinkedListItem> Items, DateTime UpdatedAtUtc);

public record LinkedListItem(Guid ShowId, string? Title, string? PosterUrl);

public record ListLink(string Token);

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
    Landing? Landing = null, SeasonVerdict? SeasonVerdict = null, WorthIt? WorthIt = null);

/// <summary>Is it worth the time? Analytics' 0-100 score, from the provider's rating and the factors that move it.</summary>
/// <param name="Verdict">"worth-it", "mostly", "mixed" or "hard-to-recommend".</param>
/// <param name="Value">The rating plus every factor's points, on the rating's scale.</param>
/// <param name="StillRunning">The ending isn't in the score yet.</param>
public record WorthIt(int Score, string Verdict, double Rating, double Value, IReadOnlyList<WorthItFactor> Factors, int RuntimeMinutes,
    bool StillRunning);

/// <param name="Key">"start", "decline", "ending" or "consistency".</param>
/// <param name="Points">What it adds to the rating; negative takes away.</param>
public record WorthItFactor(string Key, double Points, string Text);

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

/// <summary>Episode duels for one show: the next pair to compare and the user's ranking so far.</summary>
public record DuelState(int Watched, int Duels, DuelPair? Pair, IReadOnlyList<DuelRank> Ranking);

public record DuelPair(Guid First, Guid Second);

/// <param name="Score">0-100: the chance it beats an average episode of the show, by the user's answers.</param>
public record DuelRank(Guid EpisodeId, int Score, int Wins, int Losses);

/// <summary>Running shows the user is caught up on, soonest back first.</summary>
public record WaitingRoom(IReadOnlyList<WaitingShow> Shows);

/// <param name="Kind">episode, season, forecast or unknown.</param>
public record WaitingShow(Guid ShowId, string Title, string? PosterUrl, string? Network, string Kind, DateTimeOffset? BackAtUtc,
    int? SeasonNumber, DateOnly? Likely, DateOnly? Earliest, DateOnly? Latest, bool Overdue, double? Renewal, DateTimeOffset? LastAiredUtc);

/// <summary>A mixtape: three to five shows, and the shows that fit all of them.</summary>
public record Mixtape(IReadOnlyList<ShowSummary> Shows, bool Indexed, IReadOnlyList<string> SharedGenres, IReadOnlyList<MixtapeMatch> Matches,
    bool Loose);

/// <param name="Links">How it relates to each of the mixtape's shows, in the same order.</param>
public record MixtapeMatch(ShowSummary Show, int Fit, IReadOnlyList<MixtapeLink> Links);

public record MixtapeLink(Guid ShowId, int Closeness, IReadOnlyList<string> SharedGenres);

/// <summary>What the caller's plan allows for lists, as Tracking enforces it.</summary>
/// <param name="MaxLists">Lists the plan allows; null for no plan limit.</param>
/// <param name="Lists">Lists they have; can be over MaxLists when made before the limits.</param>
/// <param name="Enforced">False while the plans flag is off: no limits.</param>
public record ListLimits(string Plan, int? MaxLists, bool ShareLists, int Lists, bool Enforced)
{
    public bool AtLimit => Enforced && MaxLists is { } max && Lists >= max;
    public bool CanShare => !Enforced || ShareLists;
}

/// <summary>The caller's plan, from Identity.</summary>
/// <param name="Plan">The plan they get: their own, or the family plan they're in.</param>
/// <param name="Purchased">The plan they pay for themselves.</param>
public record UserPlan(string Plan, string Name, int? MaxLists, bool ShareLists, DateTime? ChangedAtUtc, string Purchased = "free",
    bool TrackServices = false);

/// <param name="Role">"owner", "member" or "none".</param>
/// <param name="Active">Whether the owner pays for the family plan now.</param>
/// <param name="InviteToken">The invite link's secret, for the owner; null while invites are off.</param>
public record FamilyInfo(string Role, bool Active, string? OwnerName, IReadOnlyList<FamilyMemberInfo> Members, int Seats, string? InviteToken);

/// <param name="UserId">Given to the owner only, to remove them.</param>
public record FamilyMemberInfo(string? UserId, string DisplayName, DateTime JoinedAtUtc);

public record FamilyInvite(string OwnerName, int SeatsLeft, bool Active);

public record FamilyInviteLink(string Token);

/// <summary>A user on the admins' plans page.</summary>
public record PlanUser(string UserId, string DisplayName, string? Email, string Plan, DateTime? PlanChangedAtUtc);

/// <summary>The streaming services behind the networks, with logos and estimated prices.</summary>
public record ServiceDirectory(string PricesAsOf, string PricesSource, IReadOnlyList<ServiceInfo> Services)
{
    public ServiceInfo? ForNetwork(string network) =>
        Services.FirstOrDefault(s => s.Networks.Contains(network, StringComparer.OrdinalIgnoreCase));
}

/// <param name="MonthlyUsd">Cheapest US plan a month; 0 free; null unknown.</param>
/// <param name="Logo">The icon as a data: URI; null when it couldn't be fetched.</param>
public record ServiceInfo(string Key, string Name, decimal? MonthlyUsd, string? Note, string? Logo, IReadOnlyList<string> Networks);

/// <param name="Allowed">Whether the user's plan lets them keep services.</param>
public record MyServices(string Plan, bool Allowed, IReadOnlyList<string> Services);

/// <summary>What the user's services cost per hour they watch, over the last and next WindowDays.</summary>
public record ServicesReport(int WindowDays, decimal MonthlyUsd, int UnknownPrices, int Minutes, IReadOnlyList<ServiceUse> Services,
    IReadOnlyList<ServiceUse> Missing, string PricesAsOf);

/// <param name="Verdict">"value", "pricey", "quiet" or "pause".</param>
public record ServiceUse(string Key, string Name, decimal? MonthlyUsd, int Minutes, decimal? CostPerHour, string Verdict,
    IReadOnlyList<ServiceShow> Watched, IReadOnlyList<ServiceShow> Coming);

public record ServiceShow(Guid ShowId, string Title, int Minutes, int Episodes, DateTimeOffset? NextAirsAtUtc);

/// <summary>Guess the show: today's puzzle, without its answer.</summary>
public record Puzzle(int Number, DateOnly Date, int MaxGuesses, IReadOnlyList<PuzzleClue> Clues);

/// <param name="AfterWrong">How many wrong guesses unlock it.</param>
public record PuzzleClue(string Key, int AfterWrong, string Text);

/// <param name="Answer">Set once the puzzle is over.</param>
public record PuzzleGuess(bool Correct, PuzzleFeedback Feedback, IReadOnlyList<PuzzleClue> Clues, PuzzleShow? Answer);

/// <param name="Year">"same", "later" or "earlier": when the answer premiered, against the guess.</param>
/// <param name="Network">"same", "service" (same streaming service) or "different".</param>
/// <param name="Seasons">"same", "more" or "fewer": the answer's seasons, against the guess.</param>
public record PuzzleFeedback(PuzzleShow Guess, int? GuessYear, string Year, bool YearClose, string? GuessNetwork, string Network,
    IReadOnlyList<PuzzleGenre> Genres, string? GuessKind, bool SameKind, int GuessSeasons, string Seasons, bool SeasonsClose,
    string GuessStatus, bool SameStatus, IReadOnlyList<string> SharedCast);

public record PuzzleGenre(string Genre, bool Shared);

public record PuzzleShow(Guid Id, string Title, string? PosterUrl);

/// <summary>Call it on a show: its next season to call, the caller's call, and once called, the model's and the crowd's.</summary>
/// <param name="SeasonNumber">The season open to a call; null for an ended show.</param>
/// <param name="Crowd">Everyone's average call; null under 5 callers or before the caller has called.</param>
public record ShowCall(int? SeasonNumber, DateTimeOffset? PremieresAtUtc, double? Mine, CallRange? Model, CrowdCall? Crowd,
    IReadOnlyList<SeasonCallInfo> Past);

public record CallRange(double Rating, double Low, double High);

public record CrowdCall(double Rating, int Callers);

/// <param name="State">"open", "airing" or "scored".</param>
/// <param name="BeatModel">Scored with a model: true when the call was closer, false when the model was; null on a tie.</param>
public record SeasonCallInfo(Guid ShowId, string Title, string? PosterUrl, int SeasonNumber, double Rating, double? Model, double? Actual,
    string State, bool? BeatModel, DateTimeOffset? PremieresAtUtc);

public record CallRecord(int Scored, double? AverageMiss, double? ModelAverageMiss, int BeatModel, int ModelWon);

public record MyCalls(CallRecord Record, IReadOnlyList<SeasonCallInfo> Calls, IReadOnlyList<SeasonCallInfo> Suggestions);

/// <summary>Six degrees: the shortest chain of shared actors from one show to another.</summary>
/// <param name="OutsideCatalog">Shows along the chain that aren't in the catalog.</param>
public record DegreesResult(DegreesShow From, DegreesShow To, int Degrees, IReadOnlyList<DegreesLink> Links, int OutsideCatalog);

/// <param name="ShowId">Null for a show known only from an actor's credits.</param>
public record DegreesShow(Guid? ShowId, string Title, string? PosterUrl, int? Year);

public record DegreesLink(Guid PersonId, string Name, string? PhotoUrl, DegreesShow From, string? CharacterInFrom, DegreesShow To, string? CharacterInTo);

/// <summary>Two connected shows and how many actors apart they are, without the chain.</summary>
public record DegreesChallenge(DegreesShow From, DegreesShow To, int Degrees);

/// <summary>Shows' milestone episodes (100th, 200th, …) coming up and just aired.</summary>
public record Milestones(IReadOnlyList<Milestone> Upcoming, IReadOnlyList<Milestone> Reached);

/// <param name="Number">Its place among the show's regular episodes, by the catalog's count.</param>
public record Milestone(Guid ShowId, string Title, string? PosterUrl, int Number, int Season, int Episode, DateTimeOffset AirsAtUtc, bool Story);

/// <summary>The catalog's actors born on one day of the year, best known first.</summary>
public record Birthdays(DateOnly Date, IReadOnlyList<Birthday> People);

/// <param name="Turns">Their age on the day asked about: what they turn, or would have turned.</param>
/// <param name="Milestone">A round birthday (every ten years from 30) for the living.</param>
public record Birthday(Guid PersonId, string Name, string? PhotoUrl, DateOnly Born, DateOnly? Died, int Turns, bool Milestone, BirthdayRole KnownFor,
    int Shows);

public record BirthdayRole(Guid ShowId, string Title, string? PosterUrl, string? Character);

/// <summary>Every regular episode's rating by season, with the best and weakest episodes and the best run.</summary>
public record EpisodeGrid(Guid ShowId, IReadOnlyList<GridSeason> Seasons, GridCell? Best, GridCell? Weakest, GridRun? BestRun);

/// <param name="Average">The mean of its rated episodes, from three up.</param>
public record GridSeason(int Number, double? Average, IReadOnlyList<GridCell> Episodes);

public record GridCell(Guid EpisodeId, int Season, int Number, string Title, double? Rating, bool Aired);

public record GridRun(GridCell From, GridCell To, double Average);

/// <summary>A show's episode-title formula: the words most of its titles open with.</summary>
/// <param name="Kind">"phrase", "numbered" or "article".</param>
/// <param name="Exceptions">Titles that break it, often the notable episodes.</param>
public record TitleFormula(Guid ShowId, string Kind, string Prefix, int Matching, int Titled, IReadOnlyList<FormulaTitle> Examples,
    IReadOnlyList<FormulaTitle> Exceptions);

public record FormulaTitle(Guid EpisodeId, int Season, int Number, string Title);

public record TitleFormulaLists(IReadOnlyList<FormulaShow> Phrases, IReadOnlyList<FormulaShow> Numbered, IReadOnlyList<FormulaShow> Articles);

public record FormulaShow(Guid ShowId, string Title, string? PosterUrl, string Prefix, int Matching, int Titled, string? Example);

/// <summary>TV characters who share a first name, and how common it is.</summary>
/// <param name="Rank">Its place among all first names; null when no character has it.</param>
/// <param name="Surnames">Characters with it as their last name.</param>
public record NameReport(string Name, int Count, int? Rank, IReadOnlyList<NamedCharacter> Characters, IReadOnlyList<NamedCharacter> Surnames,
    IReadOnlyList<NameDecade> Decades);

/// <param name="ShowId">Set when the show is in the catalog.</param>
public record NamedCharacter(string Character, Guid PersonId, string Actor, Guid? ShowId, string Title, string? PosterUrl, int? Year);

public record NameDecade(int Decade, int Count, int Rank);

/// <param name="Women">Played by women, by the actor's gender on TVmaze.</param>
public record NameTop(int Characters, IReadOnlyList<NameCount> Women, IReadOnlyList<NameCount> Men, IReadOnlyList<NameDecadeTop> Decades);

public record NameCount(string Name, int Count);

public record NameDecadeTop(int Decade, int Characters, IReadOnlyList<NameCount> Women, IReadOnlyList<NameCount> Men);

/// <summary>Each main cast member's age at a show's latest episode, and who grew up on screen.</summary>
/// <param name="MedianAge">The living cast's median age at the latest episode, from four ages up.</param>
public record CastAges(Guid ShowId, DateOnly FirstAired, DateOnly LastAired, bool Ended, int? MedianAge, IReadOnlyList<CastAge> Members);

/// <param name="AgeAtLatest">At the latest episode, or at their death when that came first.</param>
/// <param name="AgeAtStart">At the first episode; given only for those who grew up on screen.</param>
public record CastAge(Guid PersonId, string Name, string? Character, string? PhotoUrl, int AgeAtLatest, int? AgeAtStart, bool GrewUp, DateOnly? DiedAt);

public record CastAgeLists(IReadOnlyList<CastAgeShow> GrewUp, IReadOnlyList<CastAgeShow> Youngest, IReadOnlyList<CastAgeShow> Oldest);

/// <param name="Members">Those who grew up on screen, in the grew-up list; otherwise empty.</param>
public record CastAgeShow(Guid ShowId, string Title, string? PosterUrl, int FirstYear, int LastYear, int? MedianAge, IReadOnlyList<CastAge> Members);

/// <summary>The waits between a show's seasons.</summary>
/// <param name="TypicalDays">The median wait, given from three waits up.</param>
/// <param name="Now">For a running show: since its last episode, and when it's back if dated.</param>
public record SeasonGaps(Guid ShowId, IReadOnlyList<SeasonGap> Gaps, int? TypicalDays, SeasonGap? Longest, WaitingNow? Now);

/// <param name="Scheduled">The next season hasn't started yet; the wait runs to its first date.</param>
public record SeasonGap(int FromSeason, int ToSeason, DateOnly LastEpisode, DateOnly NextEpisode, int Days, bool Scheduled);

public record WaitingNow(DateOnly Since, int Days, DateOnly? BackOn);

public record SeasonGapLists(IReadOnlyList<GapShow> Longest, IReadOnlyList<GapShow> Revived, IReadOnlyList<GapShow> WaitingNow);

public record GapShow(Guid ShowId, string Title, string? PosterUrl, SeasonGap? Gap, WaitingNow? Now);

/// <summary>How long a show's episodes run season by season.</summary>
/// <param name="Verdict">"longer", "shorter" or "steady": the last counted season against the first.</param>
public record EpisodeLengths(Guid ShowId, string Verdict, int FirstMinutes, int LastMinutes, SeasonJump? Jump, IReadOnlyList<SeasonLength> Seasons);

/// <param name="Minutes">The season's median episode runtime.</param>
/// <param name="Counted">Enough episodes with a runtime to count in the verdict.</param>
public record SeasonLength(int Number, int Episodes, int Minutes, int TotalMinutes, bool Counted);

public record SeasonJump(int FromSeason, int ToSeason, int FromMinutes, int ToMinutes);

public record EpisodeLengthLists(IReadOnlyList<LengthChange> Longer, IReadOnlyList<LengthChange> Shorter);

/// <param name="Change">Last against first, as a share (0.5 is half as long again).</param>
public record LengthChange(Guid ShowId, string Title, string? PosterUrl, int FirstSeason, int FirstMinutes, int LastSeason, int LastMinutes,
    double Change, SeasonJump? Jump);

/// <summary>Where a show is set and filmed, from Wikidata.</summary>
/// <param name="StandsIn">Filming places far from anywhere it's set, each with the setting it plays.</param>
public record ShowSettings(Guid ShowId, IReadOnlyList<AtlasPlace> Places, IReadOnlyList<AtlasPlace> Filmed, IReadOnlyList<StandIn> StandsIn);

public record StandIn(string PlaceId, string Name, string? Country, string Plays);

/// <summary>The TV atlas: where a set of shows is set.</summary>
/// <param name="Yours">From the user's shows, not the popular fallback.</param>
/// <param name="Placed">Of <paramref name="Shows"/>, how many have a setting on the map.</param>
/// <param name="Kind">"set" or "filmed": which the places are.</param>
/// <param name="StandIns">Filming places that play somewhere else, most shows first.</param>
public record Atlas(bool Yours, string Kind, int Shows, int Placed, IReadOnlyList<AtlasPlace> Places, IReadOnlyList<AtlasCountry> Countries,
    IReadOnlyList<StandInCity> StandIns);

public record StandInCity(string PlaceId, string Name, string? Country, IReadOnlyList<StandInShow> Shows);

public record StandInShow(Guid ShowId, string Title, string? PosterUrl, string Plays);

public record AtlasPlace(string PlaceId, string Name, string? Country, double Latitude, double Longitude, IReadOnlyList<AtlasShow> Shows);

public record AtlasShow(Guid ShowId, string Title, string? PosterUrl);

public record AtlasCountry(string Name, int Shows, int Places);

/// <summary>Your TV clock: when the user watches, from their last year of watches (bulk marks left out).</summary>
/// <param name="Grid">Minutes by weekday (Monday first) and hour.</param>
public record TvClock(bool Enough, DateOnly Since, int Episodes, int Minutes, int BulkLeftOut, IReadOnlyList<IReadOnlyList<int>> Grid,
    IReadOnlyList<ClockLabel> Labels, ClockPeak? Peak, IReadOnlyList<ClockShare> Periods, double WeekendShare, Sittings? Sittings,
    IReadOnlyList<ClockShow> Shows);

public record ClockLabel(string Key, string Name, double Share);

/// <param name="Weekday">0 Monday to 6 Sunday.</param>
public record ClockPeak(int Weekday, int Hour, int Minutes);

public record ClockShare(string Key, string Name, double Share);

public record Sittings(int Count, double AverageEpisodes, int AverageMinutes, double BingeShare, LongestSitting Longest);

public record LongestSitting(DateOnly Date, int Episodes, int Minutes, Guid ShowId, string Title, string? PosterUrl, int Shows);

public record ClockShow(Guid ShowId, string Title, string? PosterUrl, int Minutes, string Period, double PeriodShare, bool Weekend);

/// <summary>Your TV life: each year since the viewer was born, with the catalog's biggest premieres and finales and their own watching.</summary>
public record TvLife(int Born, IReadOnlyList<TvLifeYear> Years, TvLifeFacts Facts);

public record TvLifeYear(int Year, int Age, IReadOnlyList<TvLifeShow> Premieres, IReadOnlyList<TvLifeShow> Finales, TvLifeYourYear? Yours);

public record TvLifeShow(Guid ShowId, string Title, string? PosterUrl, double? Rating, bool Watched);

public record TvLifeYourYear(int Started, int Finished, int Minutes, TvLifeShow? TopShow);

public record TvLifeFacts(int OlderThanYou, TvLifeShow? Oldest, int? OldestYear, TvLifeShow? BornWith, int? BiggestYear, int TotalMinutes, int ShowsWatched);

/// <summary>Where a show's cast went next: each member's main roles since the show ended (or, running, since it started).</summary>
public record ShowAfterwards(Guid ShowId, bool Ended, DateOnly Since, int CastCount, int WentOn, int CreditsPending, BiggestRole? Biggest,
    IReadOnlyList<CastAfterwards> Cast);

public record BiggestRole(Guid PersonId, string Name, NextRole Role);

public record CastAfterwards(Guid PersonId, string Name, string? PhotoUrl, string? Character, bool CreditsFetched, IReadOnlyList<NextRole> Roles);

/// <param name="ShowId">Null for a show outside the catalog.</param>
public record NextRole(Guid? ShowId, string ExternalId, string Title, string? PosterUrl, int? Year, ShowStatus Status, string? Character,
    double Popularity, double? Rating);

/// <summary>Memorable lines from Wikiquote: a few for the show page, and which episodes have lines.</summary>
/// <param name="FromEpisode">The episode <paramref name="Lines"/> come from, when the show's page has none about the whole show.</param>
public record ShowQuotes(Guid ShowId, string PageUrl, IReadOnlyList<QuoteText> Lines, QuoteEpisode? FromEpisode, int Total,
    IReadOnlyList<QuoteEpisode> Episodes);

public record QuoteEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, int Count);

/// <param name="More">How many more Wikiquote has for the episode; the page shows a few and links the rest.</param>
public record EpisodeQuotes(Guid EpisodeId, string PageUrl, IReadOnlyList<QuoteText> Quotes, int More);

public record QuoteText(IReadOnlyList<QuoteLine> Lines);

/// <param name="Speaker">Null for a stage direction.</param>
public record QuoteLine(string? Speaker, string Text);

/// <summary>A round of Who said it?: lines from episodes the player has watched, or popular first episodes.</summary>
public record WhoSaidItRound(bool FromWatched, IReadOnlyList<WhoSaidItQuestion> Questions);

public record WhoSaidItQuestion(Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber, int EpisodeNumber,
    string Line, IReadOnlyList<string> Options, string Answer, string PageUrl);

/// <summary>Spoiler watch: the viewer's shows with recent unwatched episodes, riskiest first.</summary>
public record SpoilerWatch(IReadOnlyList<SpoilerRisk> Shows);

/// <param name="Level">"high", "medium" or "low".</param>
/// <param name="CalmsAround">When the risk should fall to low, if nothing more airs.</param>
public record SpoilerRisk(Guid ShowId, string Title, string? PosterUrl, string Level, double Score, int Unseen, SpoilerEpisode Latest,
    int Behind, int BehindMinutes, DateTimeOffset? CalmsAround, bool Started);

/// <param name="Kind">"finale", "premiere" or "episode".</param>
public record SpoilerEpisode(int SeasonNumber, int EpisodeNumber, DateTimeOffset AiredAtUtc, string Kind);

