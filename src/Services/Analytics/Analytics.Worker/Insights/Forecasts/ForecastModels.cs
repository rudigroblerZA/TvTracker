using Analytics.Data;
using TvTracker.IntegrationEvents;

namespace Analytics.Worker.Insights;

/// <summary>
/// The forecasts' models, trained each run on every show's season history and backtested on it: each model replays
/// the past (what it would have said after every past season) and keeps what it got wrong as its error band. So a
/// forecast's range is a measured one, and it stays honest as the catalog grows.
/// </summary>
public sealed class ForecastModels
{
    public required RatingForecaster Rating { get; init; }
    public required RenewalCohorts Renewal { get; init; }
    public required PremiereForecaster Premiere { get; init; }

    /// <summary>One line for the log: how each model did in its backtest.</summary>
    public string Summary => $"rating {Rating.Summary}; renewal {Renewal.Summary}; premiere {Premiere.Summary}";

    public static ForecastModels Train(InsightSources sources)
    {
        var histories = sources.SeasonHistory.Select(g => (Show: sources.Shows.GetValueOrDefault(g.Key), Seasons: g.ToList()))
            .Where(h => h.Show is not null)
            .Select(h => (Show: h.Show!, h.Seasons))
            .ToList();
        return new ForecastModels
        {
            Rating = RatingForecaster.Train(histories.Select(h => h.Seasons)),
            Renewal = RenewalCohorts.Train(histories, sources.AnnouncedSeasons),
            Premiere = PremiereForecaster.Train(histories.Select(h => h.Seasons))
        };
    }

    /// <summary>A show that has ended gets no forecasts.</summary>
    public static bool Running(ShowCopy show) => show.Status is not (ShowStatus.Ended or ShowStatus.Cancelled);

    /// <summary>The value below which a share <paramref name="q"/> of the sorted values fall.</summary>
    internal static double Quantile(IReadOnlyList<double> sorted, double q) => sorted[(int)Math.Clamp(q * (sorted.Count - 1), 0, sorted.Count - 1)];
}

/// <summary>
/// Next season's rating: 0.7 × the latest complete season's rating + 0.3 × the show's average so far.
/// <list type="bullet">
/// <item><b>Why not the trend:</b> in the backtest (1,440 seasons of 277 shows) the trend's direction was right for only
/// 45% of next seasons, worse than a coin. Seasons revert towards the show's average instead (61% of the time when the
/// latest is unusually high or low), which the 0.3 captures. Plain "same as last season" was close (typical error 0.34
/// against 0.33).</item>
/// <item><b>The range:</b> the 10th to 90th percentile of past errors, separately for shows whose seasons vary little,
/// some or a lot (the error roughly doubles from steady to volatile).</item>
/// </list>
/// </summary>
public sealed class RatingForecaster
{
    private const int MinSeasons = 3;
    private const int MinCasesPerBand = 50;
    private static readonly double[] SpreadBands = [0.15, 0.3];

    private readonly Dictionary<int, (double Low, double High)> bands;

    private RatingForecaster(Dictionary<int, (double Low, double High)> bands, int cases, double meanError)
    {
        this.bands = bands;
        Cases = cases;
        MeanError = meanError;
    }

    public int Cases { get; }
    public double MeanError { get; }
    public string Summary => $"{Cases} seasons, typical error {MeanError:0.00}";

    public static RatingForecaster Train(IEnumerable<List<SeasonRecord>> histories)
    {
        var errors = new List<(int Band, double Error)>();
        foreach (var points in histories.Select(Points))
        {
            for (var k = MinSeasons; k < points.Count; k++)
            {
                var past = points.Take(k).ToList();
                errors.Add((Band(past), points[k] - Predict(past)));
            }
        }

        var all = errors.Select(e => e.Error).Order().ToList();
        var fallback = all.Count == 0 ? (-0.5, 0.5) : (ForecastModels.Quantile(all, 0.1), ForecastModels.Quantile(all, 0.9));
        var bands = Enumerable.Range(0, SpreadBands.Length + 1).ToDictionary(b => b, b =>
        {
            var inBand = errors.Where(e => e.Band == b).Select(e => e.Error).Order().ToList();
            return inBand.Count >= MinCasesPerBand ? (ForecastModels.Quantile(inBand, 0.1), ForecastModels.Quantile(inBand, 0.9)) : fallback;
        });
        return new RatingForecaster(bands, errors.Count, errors.Count == 0 ? 0 : errors.Average(e => Math.Abs(e.Error)));
    }

    public RatingForecast? Forecast(List<SeasonRecord> seasons)
    {
        var rated = seasons.Where(s => s.Complete && s.Rating is not null).ToList();
        if (rated.Count < MinSeasons || Cases == 0)
        {
            return null;
        }

        // The season after the latest complete, rated one; skipped if that one is already complete (just unrated).
        var next = rated[^1].Number + 1;
        if (seasons.FirstOrDefault(s => s.Number == next) is { Complete: true })
        {
            return null;
        }

        var past = rated.Select(s => s.Rating!.Value).ToList();
        var rating = Predict(past);
        var (low, high) = bands[Band(past)];
        return new RatingForecast(next, Math.Round(rating, 1), Math.Round(rating + low, 1), Math.Round(rating + high, 1), Cases);
    }

    private static List<double> Points(List<SeasonRecord> seasons) =>
        seasons.Where(s => s.Complete && s.Rating is not null).Select(s => s.Rating!.Value).ToList();

    private static double Predict(List<double> past) => 0.7 * past[^1] + 0.3 * past.Average();

    private static int Band(List<double> past)
    {
        var mean = past.Average();
        var spread = Math.Sqrt(past.Sum(p => (p - mean) * (p - mean)) / past.Count);
        return SpreadBands.Count(b => spread >= b);
    }
}

/// <summary>
/// Whether a show comes back after its latest season, as a share of comparable shows that did: same rating band (half
/// a point), similar point in their run, and era.
/// <list type="bullet">
/// <item><b>Labels:</b> a complete season followed by another counts as renewed; an ended show's last season as not.
/// Running shows' latest seasons are what's forecast, so they're left out.</item>
/// <item><b>Weak, and said so:</b> the catalog is mostly popular shows, so 9 in 10 seasons were followed by another.
/// In a 5-fold backtest by show, comparable-show shares beat that overall rate, but only a little (Brier 0.071 against
/// 0.074).</item>
/// <item><b>Backing off:</b> a group needs <see cref="MinComparable"/> seasons; with fewer, the era, then the point in
/// the run, are dropped.</item>
/// </list>
/// </summary>
public sealed class RenewalCohorts
{
    private const int MinComparable = 30;

    private readonly Dictionary<string, (int Renewed, int Total)> groups;

    private RenewalCohorts(Dictionary<string, (int Renewed, int Total)> groups, int cases, double overall)
    {
        this.groups = groups;
        Cases = cases;
        Overall = overall;
    }

    public int Cases { get; }
    public double Overall { get; }
    public string Summary => $"{Cases} seasons, {Overall:P0} came back";

    public static RenewalCohorts Train(IEnumerable<(ShowCopy Show, List<SeasonRecord> Seasons)> histories, ILookup<Guid, AnnouncedSeason> announced)
    {
        var groups = new Dictionary<string, (int Renewed, int Total)>();
        var renewed = 0;
        var total = 0;
        foreach (var (show, seasons) in histories)
        {
            var ended = !ForecastModels.Running(show);
            var era = Era(seasons);
            for (var i = 0; i < seasons.Count; i++)
            {
                var season = seasons[i];
                if (!season.Complete)
                {
                    continue;
                }

                bool came;
                if (i + 1 < seasons.Count || announced[show.ShowId].Any(a => a.SeasonNumber > season.Number))
                {
                    came = true;
                }
                else if (ended)
                {
                    came = false;
                }
                else
                {
                    continue;
                }

                foreach (var (key, _) in Keys(season, i + 1, era))
                {
                    var g = groups.GetValueOrDefault(key);
                    groups[key] = (g.Renewed + (came ? 1 : 0), g.Total + 1);
                }
                renewed += came ? 1 : 0;
                total++;
            }
        }

        return new RenewalCohorts(groups, total, total == 0 ? 0 : (double)renewed / total);
    }

    /// <summary>For a running show whose latest season has fully aired and nothing later is scheduled or announced.</summary>
    public RenewalOutlook? Forecast(List<SeasonRecord> seasons, int seasonsSoFar)
    {
        var latest = seasons[^1];
        foreach (var (key, basis) in Keys(latest, seasonsSoFar, Era(seasons)))
        {
            if (groups.TryGetValue(key, out var g) && g.Total >= MinComparable)
            {
                return new RenewalOutlook(latest.Number, Math.Round((double)g.Renewed / g.Total, 2), g.Total, basis, Math.Round(Overall, 2));
            }
        }
        return null;
    }

    /// <summary>The groups a season belongs to, most specific first, each with how to describe it.</summary>
    private static IEnumerable<(string Key, string Basis)> Keys(SeasonRecord season, int seasonsSoFar, int? era)
    {
        if (season.Rating is not { } rating)
        {
            yield break;
        }

        var band = Math.Clamp(Math.Floor(rating * 2) / 2, 6, 9);
        var rated = band <= 6 ? "rated under 6.5" : band >= 9 ? "rated 9 or more" : $"rated {band:0.0}-{band + 0.5:0.0}";
        var (run, runText) = seasonsSoFar switch
        {
            1 => (1, "after their first season"),
            2 => (2, "after their second season"),
            <= 4 => (3, "after their third or fourth season"),
            <= 7 => (4, "five to seven seasons in"),
            _ => (5, "eight or more seasons in")
        };
        var eraText = era switch { 0 => "before 2015", 1 => "2015-2019", _ => "since 2020" };

        if (era is not null)
        {
            yield return ($"{band}|{run}|{era}", $"{rated} {runText}, first aired {eraText}");
        }
        yield return ($"{band}|{run}", $"{rated} {runText}");
        yield return ($"{band}", rated);
    }

    private static int? Era(List<SeasonRecord> seasons) =>
        seasons.Min(s => s.First)?.Year switch { null => null, < 2015 => 0, < 2020 => 1, _ => 2 };
}

/// <summary>
/// When the next season starts: the median of the show's last three waits between seasons, after its latest season.
/// <list type="bullet">
/// <item><b>Backtest:</b> over 2,280 waits, the median of the last three beat the last wait alone (typical miss 22 against
/// 28 days). Waits run late as often as early.</item>
/// <item><b>The range:</b> errors grow with the wait, so they're kept as ratios of (wait + 30 days); the 10th to 90th
/// percentile of those ratios gives an 80% range (about 0.65× to 1.7× on the development catalog).</item>
/// <item>Needs two past waits; one is too little to go on.</item>
/// </list>
/// </summary>
public sealed class PremiereForecaster
{
    private const int MinWaits = 2;
    private const double Offset = 30;

    private readonly (double Low, double High) ratios;

    private PremiereForecaster((double Low, double High) ratios, int cases, double typicalMissDays)
    {
        this.ratios = ratios;
        Cases = cases;
        TypicalMissDays = typicalMissDays;
    }

    public int Cases { get; }
    public double TypicalMissDays { get; }
    public string Summary => $"{Cases} waits, typical miss {TypicalMissDays:0} days";

    public static PremiereForecaster Train(IEnumerable<List<SeasonRecord>> histories)
    {
        var logRatios = new List<double>();
        var misses = new List<double>();
        foreach (var waits in histories.Select(Waits))
        {
            for (var k = MinWaits; k < waits.Count; k++)
            {
                var predicted = Predict(waits.Take(k).ToList());
                logRatios.Add(Math.Log((waits[k] + Offset) / (predicted + Offset)));
                misses.Add(Math.Abs(waits[k] - predicted));
            }
        }

        logRatios.Sort();
        misses.Sort();
        var bounds = logRatios.Count == 0 ? (0.5, 2.0) : (Math.Exp(ForecastModels.Quantile(logRatios, 0.1)), Math.Exp(ForecastModels.Quantile(logRatios, 0.9)));
        return new PremiereForecaster(bounds, logRatios.Count, misses.Count == 0 ? 0 : ForecastModels.Quantile(misses, 0.5));
    }

    public PremiereForecast? Forecast(List<SeasonRecord> seasons, DateTimeOffset now)
    {
        var waits = Waits(seasons);
        if (waits.Count < MinWaits || Cases == 0 || seasons[^1].LastAired is not { } end)
        {
            return null;
        }

        var predicted = Predict(waits);
        DateOnly After(double days) => DateOnly.FromDateTime(end.UtcDateTime.AddDays(Math.Max(0, days)));
        var latest = After((predicted + Offset) * ratios.High - Offset);
        return new PremiereForecast(seasons[^1].Number + 1, After(predicted), After((predicted + Offset) * ratios.Low - Offset), latest,
            DateOnly.FromDateTime(now.UtcDateTime) > latest, Cases);
    }

    /// <summary>Days from each season's last episode to the next one's first.</summary>
    private static List<double> Waits(List<SeasonRecord> seasons) => seasons
        .Zip(seasons.Skip(1))
        .Where(p => p.First.LastAired is not null && p.Second.First is not null)
        .Select(p => (p.Second.First!.Value - p.First.LastAired!.Value).TotalDays)
        .Where(d => d >= 0)
        .ToList();

    private static double Predict(List<double> waits)
    {
        var recent = waits.TakeLast(3).Order().ToList();
        return recent.Count % 2 == 1 ? recent[recent.Count / 2] : (recent[recent.Count / 2 - 1] + recent[recent.Count / 2]) / 2;
    }
}
