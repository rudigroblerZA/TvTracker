using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>
/// Top episodes overall (genre "") and per genre, ranked by <see cref="ShowInsightContext.ListScore"/>, from
/// <see cref="ShowInsightContext.WellRated"/> shows only. Ties go to the more popular show.
/// </summary>
public sealed class TopEpisodesList : IListInsight
{
    private const int TopEpisodesPerList = 50;

    /// <summary>A show contributes at most this many episodes to a list, so one show can't fill it.</summary>
    public const int MaxEpisodesPerShow = 2;

    public ListBuilder Begin(InsightSources sources) => new Builder();

    private sealed class Builder() : ListBuilder<TopEpisodeRow>(db => db.TopEpisodes)
    {
        private readonly List<(ShowCopy Show, EpisodeCopy Episode, (int Sum, int Count) Ours, double Score)> candidates = [];

        public override string Name => "top-episodes";

        public override void Add(ShowInsightContext show, ShowInsights insights)
        {
            if (show.WellRated)
            {
                candidates.AddRange(show.RatedAired
                    .OrderByDescending(e => e.ExternalRating)
                    .Take(MaxEpisodesPerShow)
                    .Select(e => (show.Show, e, show.OurEpisodeRatings(e.EpisodeId), show.ListScore(e))));
            }
        }

        protected override IReadOnlyCollection<TopEpisodeRow> Rows()
        {
            var ordered = candidates
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Show.Popularity)
                .ToList();

            var lists = new[] { (Genre: "", Items: ordered) }
                .Concat(ordered.SelectMany(c => c.Show.Genres).Distinct()
                    .Select(g => (Genre: g, Items: ordered.Where(c => c.Show.Genres.Contains(g)).ToList())));

            return lists
                .SelectMany(list => list.Items.Take(TopEpisodesPerList).Select((c, rank) =>
                {
                    var ours = InsightMath.Pair(null, c.Ours);
                    return new TopEpisodeRow
                    {
                        Genre = list.Genre,
                        Rank = rank,
                        EpisodeId = c.Episode.EpisodeId,
                        ShowId = c.Show.ShowId,
                        ShowTitle = c.Show.Title,
                        PosterUrl = c.Show.PosterUrl,
                        SeasonNumber = c.Episode.SeasonNumber,
                        EpisodeNumber = c.Episode.EpisodeNumber,
                        Title = c.Episode.Title,
                        AirsAtUtc = c.Episode.AirsAtUtc,
                        ExternalRating = c.Episode.ExternalRating!.Value,
                        OurRating = ours.Ours,
                        OurRatingCount = ours.OurCount
                    };
                }))
                .ToList();
        }
    }
}
