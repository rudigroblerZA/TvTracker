
namespace TvTracker.InsightsEngine;

/// <summary>
/// The data every insight reads, loaded once per run: Analytics' copy of the catalog and our users' ratings and
/// follows, already grouped the way the insights look them up. Episodes and watches are loaded per batch of shows
/// instead (<see cref="LoadShowsAsync"/>), since they're the bulk of it. An insight that needs new data adds it here.
/// </summary>
public sealed class InsightSources
{
    /// <summary>
    /// The run's clock: what has aired, what's upcoming, and how recent a follow or watch is.
    /// </summary>
    public required DateTimeOffset Now { get; init; }

    public DateTime ComputedAtUtc => Now.UtcDateTime;

    public required Dictionary<Guid, ShowCopy> Shows { get; init; }

    public required List<CastCopy> Cast { get; init; }
    public required ILookup<Guid, CastCopy> CastByShow { get; init; }

    /// <summary>
    /// Credits by <see cref="PersonKey"/>.
    /// </summary>
    public required ILookup<string, CastCopy> CastByPerson { get; init; }

    /// <summary>
    /// Credits by the provider's character id.
    /// </summary>
    public required ILookup<string, CastCopy> CastByCharacter { get; init; }

    /// <summary>
    /// Each show's most prolific maker: credited behind the camera on at least <see cref="ShowTags.ProlificMinShows"/>
    /// shows; absent when it has none.
    /// </summary>
    public required Dictionary<Guid, (CreativeCopy Creative, PersonCareerCopy Career)> ProlificMakerByShow { get; init; }

    /// <summary>
    /// Everyone credited as making a show (creators, developers, executive producers).
    /// </summary>
    public required List<CreativeCopy> Creatives { get; init; }

    /// <summary>
    /// Our users' episode stars, summed and counted per episode, before the privacy rule (see <see cref="ShowInsightContext.OurEpisodeRatings"/>).
    /// </summary>
    public required Dictionary<Guid, (int Sum, int Count)> EpisodeRatings { get; init; }

    public required Dictionary<Guid, (int Sum, int Count)> ShowRatings { get; init; }

    /// <summary>
    /// How many of our users gave each show each number of stars.
    /// </summary>
    public required ILookup<Guid, (int Stars, int Count)> ShowStarCounts { get; init; }

    public required ILookup<Guid, AnnouncedSeason> AnnouncedSeasons { get; init; }

    /// <summary>
    /// Every show's regular seasons in order, summarised: the history the forecasts learn from and backtest on
    /// (see <see cref="Forecasts"/>).
    /// </summary>
    public required ILookup<Guid, SeasonRecord> SeasonHistory { get; init; }

    /// <summary>
    /// The forecast models, trained on <see cref="SeasonHistory"/> on first use in a run.
    /// </summary>
    public ForecastModels Forecasts => field ??= ForecastModels.Train(this);

    public required ILookup<Guid, DateTime> FollowDates { get; init; }

    public required ILookup<Guid, string> FollowersByShow { get; init; }

    /// <summary>
    /// Who a credit is: the catalog's person id, so two actors who share a name aren't shown as shared cast; the name
    /// for credits stored before ids were sent (the next catalog replay fills those in).
    /// </summary>
    public static string PersonKey(CastCopy credit) => credit.PersonId?.ToString() ?? $"name:{credit.PersonName}";
}

/// <summary>
/// A regular season, summarised for the forecasts. A second season summary beside <see cref="SeasonSummary"/>: the
/// forecast models train on every show's seasons before the batches run, so these come from one SQL query over the
/// whole catalog rather than from each show's loaded episodes. The rating rule is the same (null under
/// <see cref="SeasonSummary.MinRatedEpisodes"/> rated episodes, where the other has <see cref="SeasonSummary.RatingCounts"/>).
/// </summary>
/// <param name="LastAired">The latest aired episode's air time; null before any aired.</param>
/// <param name="Rating">The provider's average for its aired episodes; null with fewer than <see cref="SeasonSummary.MinRatedEpisodes"/> rated.</param>
public record SeasonRecord(Guid ShowId, int Number, int Episodes, int Aired, DateTimeOffset? First, DateTimeOffset? LastAired, double? Rating)
{
    /// <summary>
    /// Every episode has aired.
    /// </summary>
    public bool Complete => Aired > 0 && Aired == Episodes;
}
