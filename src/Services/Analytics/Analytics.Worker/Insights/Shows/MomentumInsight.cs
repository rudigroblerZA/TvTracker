using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>
/// Current followers and new follows over the last eight weeks; none below the minimum number of followers.
/// </summary>
public sealed class MomentumInsight : IShowInsight
{
    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var followedAt = show.Sources.FollowDates[show.ShowId].ToList();
        if (followedAt.Count < InsightRules.MinUsersToShow)
        {
            return insights with { Momentum = null };
        }

        var weeks = new int[8];
        foreach (var at in followedAt)
        {
            var weeksAgo = (int)((show.Now.UtcDateTime - at).TotalDays / 7);
            if (weeksAgo is >= 0 and < 8)
            {
                weeks[7 - weeksAgo]++;
            }
        }

        return insights with { Momentum = new Momentum(followedAt.Count, weeks, weeks[4..].Sum(), weeks[..4].Sum()) };
    }
}
