namespace TvTracker.InsightsEngine;

/// <summary>
/// One part of a show's insights document (<see cref="ShowInsights"/>). The pipeline runs every registered stage in
/// registration order, each given the document so far, and stores what the last one returns. Stages compute; they
/// don't load, save or publish anything.
/// </summary>
public interface IShowInsight
{
    ShowInsights Apply(ShowInsightContext show, ShowInsights insights);
}

/// <summary>
/// One rule of the show tags (see <see cref="ShowTags"/>): the tag, or null when the show doesn't qualify. The tags
/// stage asks every registered rule in registration order, which is the order tags appear in.
/// </summary>
public interface IShowTagRule
{
    ShowTag? TagFor(ShowInsightContext show);
}
