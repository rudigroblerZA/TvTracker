using Tracking.API.Data;
using TvTracker.IntegrationEvents;

namespace Tracking.API.Apis;

public record FollowRequest(WatchStatus? Status);

public record UpdateStatusRequest(WatchStatus Status);

public record FollowDto(
    Guid ShowId,
    string? Title,
    string? PosterUrl,
    ShowStatus? ShowStatus,
    WatchStatus Status,
    int WatchedCount,
    int AiredCount,
    DateTime FollowedAtUtc);

/// <summary>
/// Marks episodes as watched. Use one of:
/// <list type="bullet">
/// <item><see cref="EpisodeIds"/> — specific episodes;</item>
/// <item><see cref="ShowId"/> + <see cref="SeasonNumber"/> — every aired episode of a season;</item>
/// <item><see cref="ShowId"/> + <see cref="UpToEpisodeId"/> — every aired episode up to and including that one;</item>
/// <item><see cref="ShowId"/> alone — every aired episode of the show.</item>
/// </list>
/// <see cref="WatchedAtUtc"/> lets the mobile app replay offline changes with their original time.
/// </summary>
public record MarkWatchedRequest(
    IReadOnlyList<Guid>? EpisodeIds,
    Guid? ShowId,
    int? SeasonNumber,
    Guid? UpToEpisodeId,
    DateTimeOffset? WatchedAtUtc);

public record MarkWatchedResult(int Added, IReadOnlyList<Guid> EpisodeIds);

public record EpisodeRefDto(
    Guid EpisodeId,
    Guid ShowId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    DateTimeOffset? AirsAtUtc,
    int? RuntimeMinutes);

public record UpNextItemDto(
    Guid ShowId,
    string ShowTitle,
    string? PosterUrl,
    EpisodeRefDto Episode,
    bool HasAired,
    int RemainingAired,
    int WatchedCount,
    int AiredCount);

public record CalendarItemDto(
    Guid ShowId,
    string ShowTitle,
    string? PosterUrl,
    EpisodeRefDto Episode,
    bool Watched);

public record SeasonProgressDto(int SeasonNumber, int EpisodeCount, int AiredCount, int WatchedCount);

public record ShowProgressDto(
    Guid ShowId,
    WatchStatus? Status,
    int EpisodeCount,
    int AiredCount,
    int WatchedCount,
    double PercentComplete,
    EpisodeRefDto? NextEpisode,
    IReadOnlyList<SeasonProgressDto> Seasons,
    IReadOnlyList<Guid> WatchedEpisodeIds);

public record GenreCountDto(string Genre, int Episodes);

public record YearStatsDto(int Year, int Episodes, int Minutes, int ShowsStarted);

public record StatsDto(
    int EpisodesWatched,
    int MinutesWatched,
    int ShowsFollowed,
    int ShowsCompleted,
    IReadOnlyList<GenreCountDto> TopGenres,
    IReadOnlyList<YearStatsDto> Years);

public record CreateListRequest(string Name, string? Description);

public record UpdateListRequest(string Name, string? Description);

public record AddListItemRequest(Guid ShowId);

public record ListSummaryDto(Guid Id, string Name, string? Description, int ItemCount, DateTime UpdatedAtUtc);

public record ListItemDto(Guid ShowId, string? Title, string? PosterUrl, int Position, DateTime AddedAtUtc);

public record ListDetailDto(Guid Id, string Name, string? Description, IReadOnlyList<ListItemDto> Items, DateTime UpdatedAtUtc);

public static class TrackingMappings
{
    public static EpisodeRefDto ToRef(this EpisodeCopy e) =>
        new(e.EpisodeId, e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirsAtUtc, e.RuntimeMinutes);

    /// <summary>Season 0 is "specials" and doesn't count towards progress.</summary>
    public static bool CountsTowardsProgress(this EpisodeCopy e) => e.SeasonNumber > 0;

    public static IOrderedEnumerable<EpisodeCopy> InWatchOrder(this IEnumerable<EpisodeCopy> episodes) =>
        episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber);
}
