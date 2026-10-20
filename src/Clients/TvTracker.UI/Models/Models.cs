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

public record SeasonSummary(int Number, string Name, int EpisodeCount, DateTimeOffset? PremieresAtUtc);

public record CastMember(string Name, string? Character, string? PhotoUrl);

public record WatchProvider(string ProviderName, WatchProviderType Type, string? Url);

public record Episode(Guid Id, Guid ShowId, int SeasonNumber, int EpisodeNumber, string Title, string? Overview,
    DateTimeOffset? AirsAtUtc, int? RuntimeMinutes, EpisodeLabel Label);

public record ShowDetail(Guid Id, string Title, string? Overview, string? PosterUrl, string? BackdropUrl, DateOnly? FirstAirDate,
    ShowStatus Status, string? Network, IReadOnlyList<string> Genres, IReadOnlyList<SeasonSummary> Seasons,
    IReadOnlyList<CastMember> Cast, string Region, IReadOnlyList<WatchProvider> WatchProviders, Episode? NextEpisode, Episode? LastEpisode);

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
