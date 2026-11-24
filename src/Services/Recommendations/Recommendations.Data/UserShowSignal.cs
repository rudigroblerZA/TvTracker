namespace Recommendations.Data;

/// <summary>
/// One user's relationship with one show: follow, episodes watched, rating.
/// </summary>
public class UserShowSignal
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public bool Followed { get; set; }
    public int EpisodesWatched { get; set; }
    public int? Rating { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
