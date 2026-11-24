using TvTracker.IntegrationEvents;

namespace Recommendations.Data;

/// <summary>
/// A show's DNA from Analytics (see <see cref="DnaTraits"/>): trait key to value, 0-1. Kept apart from
/// <see cref="ShowFeatures"/> because it comes from another service and can arrive first.
/// </summary>
public class ShowDna
{
    public Guid ShowId { get; set; }
    public Dictionary<string, double> Traits { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }
}
