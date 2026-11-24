namespace Tracking.Data;

public class Follow
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public WatchStatus Status { get; set; }
    public DateTime FollowedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
