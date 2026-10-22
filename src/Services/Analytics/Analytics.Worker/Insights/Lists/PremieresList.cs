using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>Seasons premiering within <see cref="PremiereWindowDays"/> days, from each show's <see cref="ShowInsights.NextPremiere"/>.</summary>
public sealed class PremieresList : IListInsight
{
    private const int PremiereWindowDays = 90;

    public ListBuilder Begin(InsightSources sources) => new Builder();

    private sealed class Builder() : ListBuilder<PremiereRow>(db => db.Premieres)
    {
        private readonly List<PremiereRow> rows = [];

        public override string Name => "premieres";

        public override void Add(ShowInsightContext show, ShowInsights insights)
        {
            if (insights.NextPremiere is not { } p || p.PremieresAtUtc > show.Now.AddDays(PremiereWindowDays))
            {
                return;
            }

            rows.Add(new PremiereRow
            {
                ShowId = show.ShowId,
                SeasonNumber = p.SeasonNumber,
                ShowTitle = show.Show.Title,
                PosterUrl = show.Show.PosterUrl,
                Genres = show.Show.Genres,
                PremieresAtUtc = p.PremieresAtUtc,
                ShowExternalRating = show.Show.ExternalRating,
                DaysSincePreviousSeason = p.DaysSincePreviousSeason,
                CatchUpEpisodes = p.CatchUpEpisodes,
                CatchUpRuntimeMinutes = p.CatchUpRuntimeMinutes,
                RecentFollows = insights.Momentum?.RecentFollows
            });
        }

        protected override IReadOnlyCollection<PremiereRow> Rows() => rows;
    }
}
