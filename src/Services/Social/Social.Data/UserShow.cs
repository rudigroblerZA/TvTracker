namespace Social.Data;

/// <summary>
/// A user's relation to a show, from Tracking's and Ratings' events and snapshots: followed, rated.
/// </summary>
public class UserShow
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public bool Followed { get; set; }
    public DateTime? FollowedAtUtc { get; set; }

    /// <summary>
    /// Their show rating, 1-5.
    /// </summary>
    public int? Stars { get; set; }
}
