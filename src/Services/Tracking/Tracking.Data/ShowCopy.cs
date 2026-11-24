using TvTracker.IntegrationEvents;

namespace Tracking.Data;

/// <summary>
/// Read copy of a show, kept current from Catalog events.
/// </summary>
public class ShowCopy
{
    public Guid ShowId { get; set; }
    public required string Title { get; set; }
    public string? PosterUrl { get; set; }
    public ShowStatus Status { get; set; }
    public List<string> Genres { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }
}
