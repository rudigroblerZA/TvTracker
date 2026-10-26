using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Tracking.API

public record ShowFollowedIntegrationEvent(string UserId, Guid ShowId) : IntegrationEvent;

public record ShowUnfollowedIntegrationEvent(string UserId, Guid ShowId) : IntegrationEvent;

/// <summary>One event per mark action; marking a whole season sends all its episode ids together.</summary>
/// <param name="WatchedAtUtc">
/// When the user watched them, which is earlier than the event for changes the mobile app replays after being offline.
/// Null in events published before it was added; use the event's time.
/// </param>
public record EpisodeWatchedIntegrationEvent(string UserId, Guid ShowId, IReadOnlyList<Guid> EpisodeIds, DateTimeOffset? WatchedAtUtc = null)
    : IntegrationEvent;

/// <summary>The user removed an episode's watched mark.</summary>
public record EpisodeUnwatchedIntegrationEvent(string UserId, Guid ShowId, Guid EpisodeId) : IntegrationEvent;

public record ShowCompletedIntegrationEvent(string UserId, Guid ShowId) : IntegrationEvent;

/// <summary>
/// One user's follows and watched episodes as Tracking has them, sent after start and daily. Consumers repair their
/// copies from it: with no dead-letter queue, an event lost on the way leaves a copy wrong for good otherwise.
/// </summary>
/// <param name="TakenAtUtc">When the snapshot was read. A consumer keeps anything it recorded after this.</param>
public record TrackingSnapshotIntegrationEvent(string UserId, DateTimeOffset TakenAtUtc, IReadOnlyList<SnapshotFollow> Follows,
    IReadOnlyList<SnapshotWatch> Watches) : IntegrationEvent;

public record SnapshotFollow(Guid ShowId, DateTimeOffset FollowedAtUtc);

public record SnapshotWatch(Guid ShowId, Guid EpisodeId, DateTimeOffset WatchedAtUtc);
