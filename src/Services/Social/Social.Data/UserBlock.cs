namespace Social.Data;

/// <summary>
/// A block. Either way round, the two don't see each other: not in search, pages, lists or anywhere else.
/// </summary>
public class UserBlock
{
    public required string BlockerId { get; set; }
    public required string BlockedId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
