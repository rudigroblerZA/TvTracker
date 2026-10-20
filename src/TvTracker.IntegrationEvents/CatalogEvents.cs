using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Catalog.SyncWorker (on behalf of Catalog)

public enum ShowStatus
{
    Upcoming,
    Airing,
    Renewed,
    Ended,
    Cancelled
}

/// <summary>A show was imported into the catalog for the first time.</summary>
public record ShowAddedIntegrationEvent(
    Guid ShowId,
    string Title,
    string? PosterUrl,
    ShowStatus Status,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> CastNames,
    IReadOnlyList<Guid> SimilarShowIds,
    double Popularity) : IntegrationEvent;

/// <summary>A show's details changed (title, poster, genres, keywords, cast, similar shows).</summary>
public record ShowUpdatedIntegrationEvent(
    Guid ShowId,
    string Title,
    string? PosterUrl,
    ShowStatus Status,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> CastNames,
    IReadOnlyList<Guid> SimilarShowIds,
    double Popularity) : IntegrationEvent;

/// <summary>
/// Episodes of one show were scheduled, or their details or air times changed. Times are UTC.
/// One event per sync of a show, split into batches of at most <see cref="MaxEpisodes"/>,
/// so importing a show with thousands of episodes publishes a handful of events rather than thousands.
/// </summary>
public record EpisodesScheduledIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    IReadOnlyList<ScheduledEpisode> Episodes) : IntegrationEvent
{
    public const int MaxEpisodes = 500;
}

public record ScheduledEpisode(
    Guid EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    DateTimeOffset? AirsAtUtc,
    int? RuntimeMinutes);

/// <summary>
/// One episode scheduled or changed. No longer published (replaced by <see cref="EpisodesScheduledIntegrationEvent"/>);
/// consumers keep handling it so messages published before the change still apply. Remove once none can be pending.
/// </summary>
public record EpisodeScheduledIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    Guid EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    DateTimeOffset? AirsAtUtc,
    int? RuntimeMinutes) : IntegrationEvent;

/// <summary>The episode's air time has passed.</summary>
public record EpisodeAiredIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    Guid EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title) : IntegrationEvent;

public record SeasonAnnouncedIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    int SeasonNumber,
    DateTimeOffset? PremieresAtUtc) : IntegrationEvent;

public record ShowStatusChangedIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    ShowStatus OldStatus,
    ShowStatus NewStatus) : IntegrationEvent;
