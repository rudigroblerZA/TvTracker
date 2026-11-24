namespace Tracking.Data;

public class UserList
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// Shared lists are sent to Social, which shows them to the people who can see the owner.
    /// </summary>
    public bool Shared { get; set; }

    /// <summary>
    /// The secret in the list's link (<c>/l/{token}</c>), which anyone can open, signed in or not; null while it has no
    /// link. 128 random bits, so links can't be guessed; turning the link off clears it, and a new link gets a new one.
    /// </summary>
    public string? LinkToken { get; set; }

    public List<UserListItem> Items { get; set; } = [];
}
