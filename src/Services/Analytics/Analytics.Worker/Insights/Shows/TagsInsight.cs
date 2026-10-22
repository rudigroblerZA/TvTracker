using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>The show's tags: every <see cref="IShowTagRule"/> that applies, in registration order.</summary>
public sealed class TagsInsight(IEnumerable<IShowTagRule> rules) : IShowInsight
{
    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights) =>
        insights with { Tags = rules.Select(r => r.TagFor(show)).OfType<ShowTag>().ToList() };
}
