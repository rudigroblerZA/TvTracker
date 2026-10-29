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
/// <param name="Plan">One of <see cref="Plans"/>; null from senders before plans existed.</param>
/// <param name="PlanChangedAtUtc">When the plan was last set; null while it has never changed (free since sign-up).</param>
public record UserProfileSnapshotIntegrationEvent(string UserId, string DisplayName, string? Plan = null, DateTimeOffset? PlanChangedAtUtc = null)
    : IntegrationEvent;

/// <summary>A user's plan changed (<see cref="Plans"/>). Copies keep the latest by <paramref name="ChangedAtUtc"/>.</summary>
public record UserPlanChangedIntegrationEvent(string UserId, string Plan, DateTimeOffset ChangedAtUtc) : IntegrationEvent;
