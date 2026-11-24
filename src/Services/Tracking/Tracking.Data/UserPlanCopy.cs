namespace Tracking.Data;

/// <summary>
/// Copy of a user's plan (<see cref="TvTracker.IntegrationEvents.Plans"/>), from Identity's plan events and daily
/// profile snapshot, for the lists' limits. No row means free.
/// </summary>
public class UserPlanCopy
{
    public required string UserId { get; set; }
    public required string Plan { get; set; }

    /// <summary>
    /// When Identity set it; an event older than this is ignored, since events can arrive out of order.
    /// </summary>
    public DateTimeOffset ChangedAtUtc { get; set; }
}
