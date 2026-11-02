using Analytics.Worker.Insights;

namespace Analytics.UnitTests;

/// <summary>
/// Next season's rating and premiere (see CONTEXT.md, Forecasts).
/// </summary>
public class ForecastTests
{
    private static readonly Guid Show = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2018, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rating_leans_on_the_last_season_and_reverts_towards_the_average()
    {
        var model = RatingForecaster.Train([Rated(8.0, 8.0, 8.0, 8.0)]);

        var forecast = model.Forecast(Rated(7.0, 8.0, 9.0));

        // 0.7 × 9.0 + 0.3 × 8.0; the range is the backtest's misses, none here.
        Assert.Equal(new(4, 8.7, 8.7, 8.7, 1), forecast);
    }

    [Fact]
    public void No_rating_forecast_without_three_rated_seasons_or_a_backtest()
    {
        var model = RatingForecaster.Train([Rated(8.0, 8.0, 8.0, 8.0)]);

        Assert.Null(model.Forecast(Rated(8.0, 8.0)));
        Assert.Null(RatingForecaster.Train([]).Forecast(Rated(8.0, 8.0, 8.0)));
    }

    [Fact]
    public void No_rating_forecast_for_a_season_already_over_but_unrated()
    {
        var model = RatingForecaster.Train([Rated(8.0, 8.0, 8.0, 8.0)]);

        Assert.Null(model.Forecast([.. Rated(8.0, 8.0, 8.0), Season(4, null)]));
    }

    [Fact]
    public void The_premiere_is_the_median_of_the_last_three_waits()
    {
        var model = PremiereForecaster.Train([Waits(300, 300, 300)]);
        var seasons = Waits(100, 200, 300, 400);
        var end = seasons[^1].LastAired!.Value;

        var forecast = model.Forecast(seasons, end.AddDays(10));

        var likely = DateOnly.FromDateTime(end.UtcDateTime.AddDays(300));
        Assert.Equal(new(6, likely, likely, likely, false, 1), forecast);
        Assert.True(model.Forecast(seasons, end.AddDays(400))!.Overdue);
    }

    [Fact]
    public void No_premiere_forecast_with_one_wait() =>
        Assert.Null(PremiereForecaster.Train([Waits(300, 300, 300)]).Forecast(Waits(300), Start));

    private static SeasonRecord Season(int number, double? rating, DateTimeOffset? first = null, int days = 70) =>
        new(Show, number, 10, 10, first ?? Start.AddYears(number), (first ?? Start.AddYears(number)).AddDays(days), rating);

    private static List<SeasonRecord> Rated(params double[] ratings) => ratings.Select((r, i) => Season(i + 1, r)).ToList();

    /// <summary>
    /// Seasons of 70 days with the given waits between them.
    /// </summary>
    private static List<SeasonRecord> Waits(params int[] waits)
    {
        var seasons = new List<SeasonRecord> { Season(1, 8.0, Start) };
        foreach (var wait in waits)
        {
            seasons.Add(Season(seasons.Count + 1, 8.0, seasons[^1].LastAired!.Value.AddDays(wait)));
        }
        return seasons;
    }
}
