namespace Social.Data;

/// <summary>
/// A watched episode, from Tracking's events and snapshots: where people are up to in a show.
/// </summary>
public class UserWatch
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public DateTime WatchedAtUtc { get; set; }
}
