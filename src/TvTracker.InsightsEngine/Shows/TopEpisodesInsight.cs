
namespace TvTracker.InsightsEngine;

/// <summary>
/// The show's best episodes: by the provider's rating, then ours, then the earlier episode (ratings have one decimal,
/// so ties are common, and the pick must not depend on the order the episodes were loaded in).
/// </summary>
public sealed class TopEpisodesInsight : IShowInsight
{
    private const int TopEpisodesPerShow = 5;

    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights) => insights with
    {
        TopEpisodes = show.Aired
            .Where(e => e.ExternalRating is not null || show.OurEpisodeRatings(e.EpisodeId).Count >= InsightRules.MinRatingsToShow)
            .OrderByDescending(e => e.ExternalRating ?? 0)
            .ThenByDescending(e => InsightMath.Average(show.OurEpisodeRatings(e.EpisodeId)) ?? 0)
            .ThenBy(e => e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber)
            .Take(TopEpisodesPerShow)
            .Select(e => new TopEpisode(e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirsAtUtc,
                InsightMath.Pair(e.ExternalRating, show.OurEpisodeRatings(e.EpisodeId))))
            .ToList()
    };
}
