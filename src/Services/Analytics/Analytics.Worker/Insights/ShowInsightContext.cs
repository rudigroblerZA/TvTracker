using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>A season's stats and how many of its aired episodes the provider rated.</summary>
/// <param name="RatedEpisodes">Aired episodes with a provider rating.</param>
public record SeasonSummary(SeasonStats Stats, int RatedEpisodes)
{
    /// <summary>A season's rating counts for best/worst, trend, arc and finales only with at least this many rated episodes.</summary>
    public const int MinRatedEpisodes = 3;

    public bool RatingCounts => RatedEpisodes >= MinRatedEpisodes;
}

/// <summary>
/// One show as the insights see it: its episodes and watches, the run's shared <see cref="Sources"/>, and the figures
/// several insights build on (seasons, ratings, list eligibility), each worked out once on first use.
/// </summary>
public sealed class ShowInsightContext(InsightSources sources, ShowCopy show, List<EpisodeCopy> episodes, List<UserEpisodeWatch> watches)
{
    /// <summary>Shows need this many rated episodes before theirs can appear in the episode lists.</summary>
    public const int MinRatedEpisodesForLists = 5;

    /// <summary>
    /// ...and at least this share of their aired episodes rated. Sparsely rated shows get their episode ratings from a
    /// few devoted fans (e.g. episodes averaging 9.7 for a show rated 6.4), which would otherwise top every list.
    /// </summary>
    public const double MinRatedShareForLists = 0.5;

    private double? episodeMean;

    public InsightSources Sources => sources;
    public ShowCopy Show => show;
    public Guid ShowId => show.ShowId;
    public DateTimeOffset Now => sources.Now;

    /// <summary>Every episode, including those still to air.</summary>
    public List<EpisodeCopy> Episodes => episodes;

    public List<EpisodeCopy> Aired { get; } = episodes.Where(e => e.AirsAtUtc <= sources.Now).ToList();

    /// <summary>Aired episodes with a provider rating.</summary>
    public List<EpisodeCopy> RatedAired => field ??= Aired.Where(e => e.ExternalRating is not null).ToList();

    /// <summary>Our users' watched marks on this show.</summary>
    public List<UserEpisodeWatch> Watches => watches;

    /// <summary>The provider's rating and our users' stars for the show.</summary>
    public RatingPair Rating => field ??= InsightMath.Pair(show.ExternalRating, sources.ShowRatings.GetValueOrDefault(show.ShowId));

    /// <summary>Every season, in order, with its stats.</summary>
    public IReadOnlyList<SeasonSummary> Seasons => field ??= episodes
        .GroupBy(e => e.SeasonNumber)
        .OrderBy(g => g.Key)
        .Select(g =>
        {
            var seasonAired = g.Where(e => e.AirsAtUtc <= Now).ToList();
            var rated = seasonAired.Where(e => e.ExternalRating is not null).ToList();
            var seasonOurs = g.Select(e => OurEpisodeRatings(e.EpisodeId)).Aggregate((Sum: 0, Count: 0), (a, b) => (a.Sum + b.Sum, a.Count + b.Count));
            return new SeasonSummary(new SeasonStats(
                    g.Key,
                    g.Count(),
                    seasonAired.Count,
                    g.Min(e => e.AirsAtUtc),
                    g.Max(e => e.AirsAtUtc),
                    seasonAired.Sum(e => e.RuntimeMinutes ?? 0),
                    InsightMath.Pair(rated.Count > 0 ? InsightMath.Round(rated.Average(e => e.ExternalRating!.Value)) : null, seasonOurs)),
                rated.Count);
        })
        .ToList();

    public IReadOnlyList<SeasonStats> SeasonStats => field ??= Seasons.Select(s => s.Stats).ToList();

    /// <summary>
    /// The provider's ratings of the seasons that count (<see cref="SeasonSummary.RatingCounts"/>), in season order:
    /// the points behind the trend, the arc and the DNA's trajectory and peak.
    /// </summary>
    public IReadOnlyList<(int Number, double Rating)> RatedSeasons => field ??= Seasons
        .Where(s => s.Stats.Rating.External is not null && s.RatingCounts)
        .Select(s => (s.Stats.Number, s.Stats.Rating.External!.Value))
        .ToList();

    /// <summary>
    /// Least-squares slope of <see cref="RatedSeasons"/>' ratings against their order (rating points per season); null
    /// with fewer than three.
    /// </summary>
    public double? SeasonSlope
    {
        get
        {
            var points = RatedSeasons.Select((s, i) => (X: (double)i, Y: s.Rating)).ToList();
            if (points.Count < 3)
            {
                return null;
            }

            var meanX = points.Average(p => p.X);
            var meanY = points.Average(p => p.Y);
            return points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / points.Sum(p => (p.X - meanX) * (p.X - meanX));
        }
    }

    /// <summary>
    /// An episode's ratings from our users, or none when there are too few to show. Applied before any aggregation, so
    /// a hidden episode's ratings never feed a season average either (else subtracting the visible episodes would reveal them).
    /// </summary>
    public (int Sum, int Count) OurEpisodeRatings(Guid episodeId)
    {
        var ratings = sources.EpisodeRatings.GetValueOrDefault(episodeId);
        return ratings.Count >= InsightRules.MinRatingsToShow ? ratings : (0, 0);
    }

    /// <summary>
    /// Whether the show's episodes may enter the episode lists (top episodes, best finales): the show is rated, and
    /// enough of its episodes are (<see cref="MinRatedEpisodesForLists"/>, <see cref="MinRatedShareForLists"/>).
    /// </summary>
    public bool WellRated => show.ExternalRating is not null
        && RatedAired.Count >= MinRatedEpisodesForLists
        && RatedAired.Count >= Aired.Count * MinRatedShareForLists;

    /// <summary>
    /// An episode's score for the episode lists. The provider gives no vote counts, so raw episode ratings from a few
    /// voters would win. The score is the show's overall rating plus how far the episode stands above that show's
    /// typical episode: great episodes of well-regarded shows rank first. Only for <see cref="WellRated"/> shows.
    /// </summary>
    public double ListScore(EpisodeCopy episode) =>
        show.ExternalRating!.Value + (episode.ExternalRating!.Value - (episodeMean ??= RatedAired.Average(e => e.ExternalRating!.Value)));
}
