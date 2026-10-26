using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Ratings.API

public record ShowRatedIntegrationEvent(string UserId, Guid ShowId, int Stars) : IntegrationEvent;

public record EpisodeRatedIntegrationEvent(string UserId, Guid ShowId, Guid EpisodeId, int Stars) : IntegrationEvent;

/// <summary>The user removed their rating of a show.</summary>
public record ShowRatingRemovedIntegrationEvent(string UserId, Guid ShowId) : IntegrationEvent;

/// <summary>The user removed their rating of an episode.</summary>
public record EpisodeRatingRemovedIntegrationEvent(string UserId, Guid ShowId, Guid EpisodeId) : IntegrationEvent;

/// <summary>
/// One user's show and episode ratings as Ratings has them, sent after start and daily, so consumers can repair copies
/// that missed events (see <see cref="TrackingSnapshotIntegrationEvent"/>).
/// </summary>
/// <param name="TakenAtUtc">When the snapshot was read. A consumer keeps ratings it changed after this.</param>
public record RatingsSnapshotIntegrationEvent(string UserId, DateTimeOffset TakenAtUtc, IReadOnlyList<SnapshotShowRating> Shows,
    IReadOnlyList<SnapshotEpisodeRating> Episodes) : IntegrationEvent;

public record SnapshotShowRating(Guid ShowId, int Stars, DateTimeOffset RatedAtUtc);

public record SnapshotEpisodeRating(Guid ShowId, Guid EpisodeId, int Stars, DateTimeOffset RatedAtUtc);
