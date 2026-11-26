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

    /// <summary>
    /// When Tracking heard of it (the event's time, or the snapshot's): what a later snapshot goes by to tell a watch it
    /// can't know about from one whose removal was lost. Not the watch time, which an offline phone backdates.
    /// </summary>
    public DateTime RecordedAtUtc { get; set; }
}
