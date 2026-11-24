namespace Social.Data;

/// <summary>
/// An episode's numbers, for "S02E03" in the feed. Titles aren't kept: a follower may not have seen it yet.
/// </summary>
public class EpisodeCopy
{
    public EpisodeCopy()
    {
    }

    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }

    /// <summary>
    /// For counting what's aired when comparing progress.
    /// </summary>
    public DateTimeOffset? AirsAtUtc { get; set; }

    /// <summary>
    /// For planning an evening for two; null when the provider has none.
    /// </summary>
    public int? RuntimeMinutes { get; set; }
}
