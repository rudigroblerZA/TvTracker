namespace Recommendations.Data;

/// <summary>
/// A user's plan, copied from Identity's events and daily profile snapshot, for feature flags that are part of some plans
/// only (see TvTracker's <c>UserPlansBase</c>). No row means free.
/// </summary>
public class UserPlanCopy
{
    public required string UserId { get; set; }
    public required string Plan { get; set; }

    /// <summary>
    /// When Identity set it; an older event is ignored, since events can arrive out of order.
    /// </summary>
    public DateTimeOffset ChangedAtUtc { get; set; }
}
