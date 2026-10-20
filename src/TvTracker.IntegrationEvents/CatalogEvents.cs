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

/// <summary>A new episode was scheduled, or an existing episode's details or air time changed. Times are UTC.</summary>
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
