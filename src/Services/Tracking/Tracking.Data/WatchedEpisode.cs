namespace Tracking.Data;

public class WatchedEpisode
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public DateTime WatchedAtUtc { get; set; }
}
