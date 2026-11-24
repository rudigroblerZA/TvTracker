namespace Recommendations.Data;

/// <summary>
/// Users whose recommendations need rebuilding.
/// </summary>
public class DirtyUser
{
    public required string UserId { get; set; }
    public DateTime MarkedAtUtc { get; set; }
}
