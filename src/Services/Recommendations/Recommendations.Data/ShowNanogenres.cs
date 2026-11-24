namespace Recommendations.Data;

/// <summary>
/// A show's nanogenres from the catalog ("Sports comedy"), most specific first. Kept apart, like DNA, since it can arrive first.
/// </summary>
public class ShowNanogenres
{
    public Guid ShowId { get; set; }
    public List<string> Names { get; set; } = [];
}
