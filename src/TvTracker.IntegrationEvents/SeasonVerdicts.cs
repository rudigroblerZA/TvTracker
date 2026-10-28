namespace TvTracker.IntegrationEvents;

// Shared by Analytics (which gives the verdicts), Notifications (which words them) and the UI (which shows them;
// TvTracker.UI links this file), so a new verdict can't be silently read as another.

/// <summary>How a season that just ended compares with the show's other seasons (<see cref="SeasonVerdictIntegrationEvent"/>).</summary>
public static class SeasonVerdicts
{
    /// <summary>Rated above every other season (with two or more to compare).</summary>
    public const string BestYet = "best-yet";

    /// <summary>0.3 or more above the other seasons' median.</summary>
    public const string Above = "above";

    /// <summary>Within 0.3 of the other seasons' median.</summary>
    public const string InLine = "in-line";

    /// <summary>0.3 or more below the other seasons' median.</summary>
    public const string Below = "below";

    /// <summary>Rated below every other season (with two or more to compare).</summary>
    public const string WeakestYet = "weakest-yet";

    /// <summary>The show's first season, with nothing to compare.</summary>
    public const string FirstSeason = "first-season";

    /// <summary>A later season whose earlier ones have too few rated episodes to compare with.</summary>
    public const string Unranked = "unranked";
}

/// <summary>How a season's finale rated against the season.</summary>
public static class FinaleVerdicts
{
    /// <summary>The finale rated 0.3 or more above the season's average.</summary>
    public const string Strong = "strong";

    public const string InLine = "in-line";

    /// <summary>The finale rated 0.3 or more below the season's average.</summary>
    public const string Weak = "weak";
}
