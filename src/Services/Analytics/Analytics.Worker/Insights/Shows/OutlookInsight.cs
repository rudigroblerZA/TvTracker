using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>
/// Forecasts for a running show (see <see cref="ForecastModels"/>): its next season's rating and, between seasons with
/// nothing scheduled or announced, whether it's likely to come back and when. A scheduled season is a fact, and
/// already on the document as <see cref="ShowInsights.NextPremiere"/>.
/// </summary>
public sealed class OutlookInsight : IShowInsight
{
    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var seasons = show.Sources.SeasonHistory[show.ShowId].ToList();
        if (!ForecastModels.Running(show.Show) || seasons.Count == 0)
        {
            return insights with { Outlook = null };
        }

        var models = show.Sources.Forecasts;
        var latest = seasons[^1];
        var betweenSeasons = latest.Complete && !show.Sources.AnnouncedSeasons[show.ShowId].Any(a => a.SeasonNumber > latest.Number);

        var outlook = new Outlook(
            models.Rating.Forecast(seasons),
            betweenSeasons ? models.Renewal.Forecast(seasons, seasons.Count) : null,
            betweenSeasons ? models.Premiere.Forecast(seasons, show.Now) : null);
        return insights with
        {
            Outlook = outlook is { NextSeasonRating: null, Renewal: null, NextSeason: null } ? null : outlook
        };
    }
}
