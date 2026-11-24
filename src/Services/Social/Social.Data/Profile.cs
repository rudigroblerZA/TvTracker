namespace Social.Data;

/// <summary>
/// A user as others see them here: a name from Identity, and whether they share what they watch.
/// </summary>
public class Profile
{
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>
    /// Their picture, as Identity's profile events carry it: an https address, or a path under Identity.API's
    /// <c>api/v1/</c> for one they uploaded; null for none.
    /// </summary>
    public string? Avatar { get; set; }

    /// <summary>
    /// Whether the people who can see this user see their activity. On unless they turn it off.
    /// </summary>
    public bool ShareActivity { get; set; } = true;

    /// <summary>
    /// Followers need approving: until then a request is pending, and only approved followers see anything.
    /// </summary>
    public bool Private { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
