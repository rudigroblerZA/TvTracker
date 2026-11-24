namespace Social.Data;

/// <summary>
/// One user following another. One way, and following back is a separate choice. Following a private profile is a
/// request until the followee approves it.
/// </summary>
public class UserFollow
{
    public required string FollowerId { get; set; }
    public required string FolloweeId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// False while it's a request to a private profile.
    /// </summary>
    public bool Approved { get; set; } = true;
}
