namespace Social.Data;

/// <summary>
/// One thing a user did, as their followers see it in the feed. Episodes watched of one show close together are one item
/// ("watched 3 episodes"), so a binge doesn't fill the feed.
/// </summary>
public class Activity
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }

    /// <summary>
    /// One of <see cref="ActivityKinds"/>.
    /// </summary>
    public required string Kind { get; set; }

    public Guid ShowId { get; set; }

    /// <summary>
    /// For "watched": the episodes, in the order they were marked.
    /// </summary>
    public List<Guid> EpisodeIds { get; set; } = [];

    /// <summary>
    /// For "rated": the stars, 1-5.
    /// </summary>
    public int? Stars { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
