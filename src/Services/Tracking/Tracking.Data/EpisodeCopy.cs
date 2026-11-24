namespace Tracking.Data;

/// <summary>
/// Read copy of an episode, kept current from EpisodeScheduled events.
/// </summary>
public class EpisodeCopy
{
    public EpisodeCopy()
    {
    }

    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset? AirsAtUtc { get; set; }
    public int? RuntimeMinutes { get; set; }
}
