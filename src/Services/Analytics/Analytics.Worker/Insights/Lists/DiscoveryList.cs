using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>The discovery lists (see <see cref="DiscoveryLists"/>), overall (genre "") and per genre.</summary>
public sealed class DiscoveryList : IListInsight
{
    private const int ListLength = 30;

    /// <summary>
    /// Hidden gems: shows rated at least this, outside the catalog's most popular <see cref="HiddenGemPopularShare"/>
    /// on the provider (TVmaze's popularity tops out at 100 for the famous ones), and followed by fewer than the
    /// minimum number of our users.
    /// </summary>
    private const double HiddenGemMinRating = 7.5;

    private const double HiddenGemPopularShare = 0.4;

    /// <summary>The consistency lists only rank shows rated at least this, so dull-but-steady shows don't top them.</summary>
    private const double MostConsistentMinRating = 7.5;

    private const double RollercoasterMinRating = 7.0;

    /// <summary>Worth the wait: slow starters rated at least this as a show...</summary>
    private const double WorthTheWaitMinRating = 7.5;

    /// <summary>...that rate at least this much better from the turn than before it.</summary>
    private const double WorthTheWaitMinLift = 0.4;

    /// <summary>Great endings: ended shows rated at least this that stick the landing.</summary>
    private const double GreatEndingsMinRating = 7.5;

    public ListBuilder Begin(InsightSources sources) => new Builder();

    private sealed record Candidate(
        ShowCopy Show, int AiredEpisodes, bool WellRated, Consistency? Consistency, int Followers,
        List<(EpisodeCopy Finale, double Score)> Finales, IReadOnlyList<ShowTag> Tags, double? OurRating, int RuntimeMinutes,
        GetsGoodTurn? Turn, Landing? Landing)
    {
        public bool Has(string tag) => Tags.Any(t => t.Key == tag);
    }

    private sealed class Builder() : ListBuilder<DiscoveryRow>(db => db.Discovery)
    {
        private readonly List<Candidate> candidates = [];

        public override string Name => "discovery";

        public override void Add(ShowInsightContext show, ShowInsights insights)
        {
            var finales = show.WellRated
                ? FinalesOf(show)
                    .Select(f => (f, show.ListScore(f)))
                    .OrderByDescending(f => f.Item2)
                    .Take(TopEpisodesList.MaxEpisodesPerShow)
                    .ToList()
                : [];
            candidates.Add(new Candidate(show.Show, show.Aired.Count, show.WellRated, insights.Patterns?.Consistency,
                show.Sources.FollowersByShow[show.ShowId].Count(), finales, insights.Tags ?? [], insights.Rating.Ours,
                insights.Run.RuntimeMinutes, insights.GetsGood?.Turn, insights.Landing));
        }

        /// <summary>Each fully aired season's last episode, when it's rated and the season has enough rated episodes.</summary>
        private static IEnumerable<EpisodeCopy> FinalesOf(ShowInsightContext show)
        {
            var bySeason = show.Episodes.ToLookup(e => e.SeasonNumber);
            return show.Seasons
                .Where(s => s.Stats.AiredCount > 0 && s.Stats.AiredCount == s.Stats.EpisodeCount && s.RatingCounts)
                .Select(s => bySeason[s.Stats.Number].MaxBy(e => e.EpisodeNumber)!)
                .Where(e => e.ExternalRating is not null);
        }

        protected override IReadOnlyCollection<DiscoveryRow> Rows()
        {
            var genres = new[] { "" }.Concat(candidates.SelectMany(c => c.Show.Genres).Distinct());
            var rows = new List<DiscoveryRow>();

            var byPopularity = candidates.Select(c => c.Show.Popularity).OrderDescending().ToList();
            var popularCutoff = byPopularity.Count == 0 ? 0 : byPopularity[(int)(byPopularity.Count * HiddenGemPopularShare)];

            foreach (var genre in genres)
            {
                var inGenre = candidates.Where(c => genre == "" || c.Show.Genres.Contains(genre)).ToList();

                Add(DiscoveryLists.HiddenGems, genre, inGenre
                    .Where(c => c.AiredEpisodes > 0 && c.Show.ExternalRating >= HiddenGemMinRating
                        && c.Show.Popularity < popularCutoff && c.Followers < InsightRules.MinUsersToShow)
                    .OrderByDescending(c => c.Show.ExternalRating)
                    .ThenBy(c => c.Show.Popularity)
                    .Select(c => Row(c.Show, c.Show.ExternalRating!.Value)));

                Add(DiscoveryLists.MostConsistent, genre, inGenre
                    .Where(c => c.WellRated && c.Consistency is not null && c.Show.ExternalRating >= MostConsistentMinRating)
                    .OrderBy(c => c.Consistency!.Spread)
                    .ThenByDescending(c => c.Show.ExternalRating)
                    .Select(c => Row(c.Show, c.Consistency!.Spread)));

                Add(DiscoveryLists.Rollercoasters, genre, inGenre
                    .Where(c => c.WellRated && c.Consistency is not null && c.Show.ExternalRating >= RollercoasterMinRating)
                    .OrderByDescending(c => c.Consistency!.Spread)
                    .Select(c => Row(c.Show, c.Consistency!.Spread)));

                Add(DiscoveryLists.BestFinales, genre, inGenre
                    .SelectMany(c => c.Finales.Select(f => (c.Show, f.Finale, f.Score)))
                    .OrderByDescending(f => f.Score)
                    .ThenByDescending(f => f.Show.Popularity)
                    .Select(f => Row(f.Show, f.Finale.ExternalRating!.Value, f.Finale)));

                // Value: minutes to watch it all.
                Add(DiscoveryLists.Bingeable, genre, inGenre
                    .Where(c => c.Has(ShowTags.Bingeable))
                    .OrderByDescending(c => c.Show.ExternalRating ?? 0)
                    .ThenByDescending(c => c.Show.Popularity)
                    .Select(c => Row(c.Show, c.RuntimeMinutes)));

                Add(DiscoveryLists.WeekendBinges, genre, inGenre
                    .Where(c => c.Has(ShowTags.WeekendBinge))
                    .OrderByDescending(c => c.Show.ExternalRating ?? 0)
                    .ThenByDescending(c => c.Show.Popularity)
                    .Select(c => Row(c.Show, c.RuntimeMinutes)));

                // Value: our users' average stars, or 0 when it's a favourite by the provider's rating.
                Add(DiscoveryLists.FanFavorites, genre, inGenre
                    .Where(c => c.Has(ShowTags.FanFavorite))
                    .OrderByDescending(c => c.OurRating is >= ShowTags.FanFavoriteMinStars)
                    .ThenByDescending(c => c.OurRating ?? 0)
                    .ThenByDescending(c => c.Show.ExternalRating ?? 0)
                    .ThenByDescending(c => c.Show.Popularity)
                    .Select(c => Row(c.Show, c.OurRating is >= ShowTags.FanFavoriteMinStars ? c.OurRating.Value : 0)));

                // Value: how much better it rates from the turn; the episode is where it gets good. Most popular first:
                // ordered by the lift instead, thinly rated old shows with a few 9.5s led the list.
                Add(DiscoveryLists.WorthTheWait, genre, inGenre
                    .Where(c => c.Turn is { } turn && turn.After - turn.Before >= WorthTheWaitMinLift && c.Show.ExternalRating >= WorthTheWaitMinRating)
                    .OrderByDescending(c => c.Show.Popularity)
                    .Select(c =>
                    {
                        var row = Row(c.Show, Math.Round(c.Turn!.After - c.Turn.Before, 2));
                        row.EpisodeId = c.Turn.EpisodeId;
                        row.SeasonNumber = c.Turn.SeasonNumber;
                        row.EpisodeNumber = c.Turn.EpisodeNumber;
                        row.EpisodeTitle = c.Turn.Title;
                        return row;
                    }));

                // Value: the finale's rating. No episode: naming the finale would spoil it.
                Add(DiscoveryLists.GreatEndings, genre, inGenre
                    .Where(c => c.Landing is { Verdict: LandingVerdicts.Sticks } && c.Show.ExternalRating >= GreatEndingsMinRating)
                    .OrderByDescending(c => c.Show.Popularity)
                    .ThenByDescending(c => c.Landing!.FinaleRating)
                    .Select(c => Row(c.Show, c.Landing!.FinaleRating)));
            }

            return rows;

            void Add(string list, string genre, IEnumerable<DiscoveryRow> ranked)
            {
                var rank = 0;
                foreach (var row in ranked.Take(ListLength))
                {
                    row.List = list;
                    row.Genre = genre;
                    row.Rank = rank++;
                    rows.Add(row);
                }
            }

            static DiscoveryRow Row(ShowCopy show, double value, EpisodeCopy? episode = null) => new()
            {
                List = "",
                Genre = "",
                ShowId = show.ShowId,
                ShowTitle = show.Title,
                PosterUrl = show.PosterUrl,
                ShowExternalRating = show.ExternalRating,
                Value = value,
                EpisodeId = episode?.EpisodeId,
                SeasonNumber = episode?.SeasonNumber,
                EpisodeNumber = episode?.EpisodeNumber,
                EpisodeTitle = episode?.Title
            };
        }
    }
}
