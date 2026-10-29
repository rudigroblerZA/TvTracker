namespace TvTracker.IntegrationEvents;

// Shared by the catalog (Mixtape, nanogenres) and Recommendations, which compare shows by kind, so all of them read
// the same keywords as a kind.

/// <summary>
/// The kinds of show the provider tags (TVmaze's type), as the catalog keeps them in a show's keywords. A show has at
/// most one; one with none has no known kind.
/// </summary>
public static class ShowKinds
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        "scripted", "animation", "reality", "documentary", "talk show", "variety", "game show", "news", "sports",
        "award show", "panel show"
    };

    /// <summary>The show's kind from its keywords, or null when it has none.</summary>
    public static string? Of(IEnumerable<string> keywords) => keywords.FirstOrDefault(All.Contains);
}
