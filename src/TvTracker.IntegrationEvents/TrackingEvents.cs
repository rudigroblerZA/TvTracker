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
/// <param name="SharedLists">The lists they share; null from publishers that predate list sharing.</param>
public record TrackingSnapshotIntegrationEvent(string UserId, DateTimeOffset TakenAtUtc, IReadOnlyList<SnapshotFollow> Follows,
    IReadOnlyList<SnapshotWatch> Watches, IReadOnlyList<SharedListSnapshot>? SharedLists = null) : IntegrationEvent;

public record SnapshotFollow(Guid ShowId, DateTimeOffset FollowedAtUtc);

public record SnapshotWatch(Guid ShowId, Guid EpisodeId, DateTimeOffset WatchedAtUtc);

public record SharedListSnapshot(Guid ListId, string Name, string? Description, IReadOnlyList<Guid> ShowIds, DateTimeOffset UpdatedAtUtc);

/// <summary>
/// A shared list as it now is: sent when it's shared and whenever a shared list changes. Social shows it to the people
/// who can see its owner. <paramref name="UpdatedAtUtc"/> orders updates that arrive out of order.
/// </summary>
public record ListSharedIntegrationEvent(Guid ListId, string UserId, string Name, string? Description, IReadOnlyList<Guid> ShowIds,
    DateTimeOffset UpdatedAtUtc) : IntegrationEvent;

/// <summary>A list stopped being shared, or was deleted.</summary>
public record ListUnsharedIntegrationEvent(Guid ListId, string UserId, DateTimeOffset UpdatedAtUtc) : IntegrationEvent;
