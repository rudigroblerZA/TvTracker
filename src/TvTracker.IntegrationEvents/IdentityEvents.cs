using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Identity.API

public record UserRegisteredIntegrationEvent(string UserId, string DisplayName, string? Email) : IntegrationEvent;

public record UserProfileUpdatedIntegrationEvent(
    string UserId,
    string DisplayName,
    string? Email,
    string TimeZone,
    string Region,
    string Language) : IntegrationEvent;

/// <summary>Every service deletes that user's data when it receives this.</summary>
public record UserDeletedIntegrationEvent(string UserId) : IntegrationEvent;
