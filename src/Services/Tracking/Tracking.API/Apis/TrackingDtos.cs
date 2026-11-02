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

/// <summary>Watches from an export (Bring your history), each with the time it was watched.</summary>
public record ImportWatchesRequest(IReadOnlyList<ImportedWatch> Watches)
{
    public const int MaxWatches = 500;
}

public record ImportedWatch(Guid EpisodeId, DateTimeOffset WatchedAtUtc);

/// <param name="Added">Watches added; ones already marked aren't counted.</param>
public record ImportWatchesResult(int Added);

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

/// <param name="Shared">Share the list with the people who can see you on Social; null leaves it as it is.</param>
public record UpdateListRequest(string Name, string? Description, bool? Shared = null);

public record AddListItemRequest(Guid ShowId);

public record ListSummaryDto(Guid Id, string Name, string? Description, int ItemCount, DateTime UpdatedAtUtc, bool Shared = false);

public record ListItemDto(Guid ShowId, string? Title, string? PosterUrl, int Position, DateTime AddedAtUtc);

/// <param name="LinkToken">The list's link secret, for its owner; null without a link.</param>
public record ListDetailDto(Guid Id, string Name, string? Description, IReadOnlyList<ListItemDto> Items, DateTime UpdatedAtUtc, bool Shared = false,
    string? LinkToken = null);

/// <summary>A list as its link shows it: no owner, no ids beyond the shows'.</summary>
public record LinkedListDto(string Name, string? Description, IReadOnlyList<LinkedListItemDto> Items, DateTime UpdatedAtUtc);

public record LinkedListItemDto(Guid ShowId, string? Title, string? PosterUrl);

public record ListLinkDto(string Token);

public static class TrackingMappings
{
    public static EpisodeRefDto ToRef(this EpisodeCopy e) =>
        new(e.EpisodeId, e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirsAtUtc, e.RuntimeMinutes);

    /// <summary>Season 0 is "specials" and doesn't count towards progress.</summary>
    public static bool CountsTowardsProgress(this EpisodeCopy e) => e.SeasonNumber > 0;

    public static IOrderedEnumerable<EpisodeCopy> InWatchOrder(this IEnumerable<EpisodeCopy> episodes) =>
        episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber);
}

/// <param name="Plan">The caller's plan (<see cref="TvTracker.IntegrationEvents.Plans"/>).</param>
/// <param name="MaxLists">Lists their plan allows; null for no plan limit.</param>
/// <param name="Lists">Lists they have; can be over <paramref name="MaxLists"/> when made before the limits.</param>
/// <param name="Enforced">False while the plans flag is off for them: no plan limits apply.</param>
public record ListLimitsDto(string Plan, int? MaxLists, bool ShareLists, int Lists, bool Enforced);
