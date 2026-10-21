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
