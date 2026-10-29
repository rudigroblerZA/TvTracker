namespace TvTracker.IntegrationEvents;

// Shared by Identity (which owns each user's plan), the services that enforce a plan's limits (Tracking, Analytics) and
// the UI (which links this file), so a limit is defined once and every side says the same.

/// <summary>
/// What a user pays for. Identity owns the plan and sends each user's effective plan (their own, or the family plan they
/// belong to) as <see cref="UserPlanChangedIntegrationEvent"/> and in the daily profile snapshot; a service that
/// enforces a limit keeps a copy.
/// </summary>
public static class Plans
{
    public const string Free = "free";
    public const string Plus = "plus";

    /// <summary>Plus for the owner and up to <see cref="FamilyMembers"/> people they invite, on one subscription.</summary>
    public const string Family = "family";

    public static readonly IReadOnlyList<string> All = [Free, Plus, Family];

    /// <summary>People a family plan's owner can invite, besides themselves.</summary>
    public const int FamilyMembers = 5;

    private static readonly PlanLimits FreeLimits = new(MaxLists: 3, ShareLists: true, TrackServices: false);
    private static readonly PlanLimits PlusLimits = new(MaxLists: null, ShareLists: true, TrackServices: true);

    /// <summary>The plan's limits; an unknown plan (or none yet) is free. Family has Plus's.</summary>
    public static PlanLimits Limits(string? plan) => plan is Plus or Family ? PlusLimits : FreeLimits;

    /// <summary>"Free", "Plus" or "Family", as the UI and error messages name the plan.</summary>
    public static string Name(string? plan) => plan switch
    {
        Plus => "Plus",
        Family => "Family",
        _ => "Free"
    };
}

/// <param name="MaxLists">How many custom lists the user may have; null for no plan limit (the service's own cap still applies).</param>
/// <param name="ShareLists">
/// Whether they may share a list, on their profile or by link. Every plan may now (sharing brings people in); the
/// limit stays so a plan can take it away again without new plumbing.
/// </param>
/// <param name="TrackServices">Whether they may keep the services they pay for, and see what each costs per hour watched.</param>
public record PlanLimits(int? MaxLists, bool ShareLists, bool TrackServices = false);
