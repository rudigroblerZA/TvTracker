using TvTracker.IntegrationEvents;

namespace Recommendations.Data;

/// <summary>
/// What Recommendations knows about a show, from ShowAdded/ShowUpdated.
/// </summary>
public class ShowFeatures
{
    public Guid ShowId { get; set; }
    public required string Title { get; set; }
    public string? PosterUrl { get; set; }
    public ShowStatus Status { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Keywords { get; set; } = [];
    public List<string> CastNames { get; set; } = [];

    /// <summary>
    /// The catalog provider's "similar shows", most similar first.
    /// </summary>
    public List<Guid> SimilarShowIds { get; set; } = [];

    public double Popularity { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
