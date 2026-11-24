namespace Tracking.Data;

public class UserListItem
{
    public Guid ListId { get; set; }
    public Guid ShowId { get; set; }
    public int Position { get; set; }
    public DateTime AddedAtUtc { get; set; }
}
