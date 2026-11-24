namespace Recommendations.Data;

/// <summary>
/// Pre-computed "for you" list, rebuilt in the background when the user's signals change.
/// </summary>
public class UserRecommendation
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public int Rank { get; set; }
    public double Score { get; set; }
    public required string Reason { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}
