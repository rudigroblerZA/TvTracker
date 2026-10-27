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

/// <summary>
/// A user's profile as Identity has it, sent after start and daily. Services that started after a user registered
/// (Social) learn their name from it, and a lost profile event heals.
/// </summary>
public record UserProfileSnapshotIntegrationEvent(string UserId, string DisplayName) : IntegrationEvent;
