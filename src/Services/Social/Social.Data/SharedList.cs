namespace Social.Data;

/// <summary>
/// A list its owner shares, copied from Tracking. Who can see it follows Social's rules (blocks, private profiles).
/// </summary>
public class SharedList
{
    public Guid ListId { get; set; }
    public required string UserId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<Guid> ShowIds { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }
}
