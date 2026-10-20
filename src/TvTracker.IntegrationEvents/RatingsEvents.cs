using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Ratings.API

public record ShowRatedIntegrationEvent(string UserId, Guid ShowId, int Stars) : IntegrationEvent;

public record EpisodeRatedIntegrationEvent(string UserId, Guid ShowId, Guid EpisodeId, int Stars) : IntegrationEvent;
