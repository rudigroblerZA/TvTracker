namespace TvTracker.IntegrationEvents;

// Shared by Identity (which owns each user's plan), the services that enforce a plan's limits (Tracking) and the UI
// (which links this file), so a limit is defined once and every side says the same.

/// <summary>
/// What a user pays for. Identity owns the plan and sends it as <see cref="UserPlanChangedIntegrationEvent"/> and in
/// the daily profile snapshot; a service that enforces a limit keeps a copy.
/// </summary>
public static class Plans
{
    public const string Free = "free";
    public const string Plus = "plus";

    public static readonly IReadOnlyList<string> All = [Free, Plus];

    private static readonly PlanLimits FreeLimits = new(MaxLists: 3, ShareLists: false);
    private static readonly PlanLimits PlusLimits = new(MaxLists: null, ShareLists: true);

    /// <summary>The plan's limits; an unknown plan (or none yet) is free.</summary>
    public static PlanLimits Limits(string? plan) => plan == Plus ? PlusLimits : FreeLimits;

    /// <summary>"Free" or "Plus", as the UI and error messages name the plan.</summary>
    public static string Name(string? plan) => plan == Plus ? "Plus" : "Free";
}

/// <param name="MaxLists">How many custom lists the user may have; null for no plan limit (the service's own cap still applies).</param>
/// <param name="ShareLists">Whether they may share a list on their profile.</param>
public record PlanLimits(int? MaxLists, bool ShareLists);
