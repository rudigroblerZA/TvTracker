using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Tracking.API

public record ShowFollowedIntegrationEvent(string UserId, Guid ShowId) : IntegrationEvent;

public record ShowUnfollowedIntegrationEvent(string UserId, Guid ShowId) : IntegrationEvent;

/// <summary>One event per mark action; marking a whole season sends all its episode ids together.</summary>
public record EpisodeWatchedIntegrationEvent(string UserId, Guid ShowId, IReadOnlyList<Guid> EpisodeIds) : IntegrationEvent;

public record ShowCompletedIntegrationEvent(string UserId, Guid ShowId) : IntegrationEvent;
