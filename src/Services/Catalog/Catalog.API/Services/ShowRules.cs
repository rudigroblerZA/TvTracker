namespace Catalog.API.Services;

/// <summary>
/// Readings of the catalog that several features share, so they agree on which shows they mean.
/// </summary>
public static class ShowRules
{
    /// <summary>
    /// A daily show has a regular season of more than this many episodes: morning shows, talk shows, soaps, donghua.
    /// Episode lengths, Between seasons, Title formulas, the episode grid, Holiday episodes and Milestones leave them out,
    /// since their dated episodes crowd out everything else.
    /// </summary>
    public const int DailySeason = 60;

    /// <summary>
    /// The kinds (<see cref="TvTracker.IntegrationEvents.ShowKinds"/>) that tell a story: scripted and animated shows.
    /// An array, so EF translates <c>s.Keywords.Any(k =&gt; StoryKinds.Contains(k))</c> to an array overlap.
    /// </summary>
    public static readonly string[] StoryKinds = ["scripted", "animation"];
}
