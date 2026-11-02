using Analytics.Data;
using Analytics.Worker.Insights;
using TvTracker.IntegrationEvents;

namespace Analytics.UnitTests;

/// <summary>In-memory copies of shows and episodes, and the worker's per-show context built over them.</summary>
internal static class Build
{
    public static ShowCopy Show(ShowStatus status = ShowStatus.Airing, double? rating = 8.0, params string[] genres) => new()
    {
        ShowId = Guid.NewGuid(), Title = "Test show", Status = status, ExternalRating = rating, Genres = [.. genres]
    };

    /// <summary>A weekly season: one episode every 7 days from <paramref name="first"/>, rated as given.</summary>
    public static List<EpisodeCopy> Season(ShowCopy show, int number, DateTimeOffset first, params double?[] ratings) =>
        ratings.Select((r, i) => Episode(show, number, i + 1, first.AddDays(7 * i), r)).ToList();

    public static EpisodeCopy Episode(ShowCopy show, int season, int number, DateTimeOffset? airs, double? rating, string? title = null,
        int runtime = 45) => new()
    {
        EpisodeId = Guid.NewGuid(), ShowId = show.ShowId, SeasonNumber = season, EpisodeNumber = number,
        Title = title ?? $"Episode title {season}.{number}", AirsAtUtc = airs, RuntimeMinutes = runtime, ExternalRating = rating
    };

    public static ShowInsightContext Context(DateTimeOffset now, ShowCopy show, IEnumerable<EpisodeCopy> episodes,
        IEnumerable<AnnouncedSeason>? announced = null) =>
        new(Sources(now, show, announced ?? []), show, episodes.ToList(), []);

    /// <summary>The run's shared data, with nothing about our users.</summary>
    public static InsightSources Sources(DateTimeOffset now, ShowCopy show, IEnumerable<AnnouncedSeason> announced) => new()
    {
        Now = now,
        Shows = new() { [show.ShowId] = show },
        Cast = [],
        CastByShow = Empty<Guid, CastCopy>(),
        CastByPerson = Empty<string, CastCopy>(),
        CastByCharacter = Empty<string, CastCopy>(),
        ProlificMakerByShow = [],
        Creatives = [],
        EpisodeRatings = [],
        ShowRatings = [],
        ShowStarCounts = Empty<Guid, (int Stars, int Count)>(),
        AnnouncedSeasons = announced.ToLookup(a => a.ShowId),
        SeasonHistory = Empty<Guid, SeasonRecord>(),
        FollowDates = Empty<Guid, DateTime>(),
        FollowersByShow = Empty<Guid, string>()
    };

    /// <summary>A show document before any stage has run, as the pipeline starts one.</summary>
    public static ShowInsights Document(ShowCopy show, DateTimeOffset now) =>
        new(show.ShowId, show.Title, now.UtcDateTime, new RatingPair(show.ExternalRating, null, 0), [], null, null, null, [],
            new RunFacts(0, 0, 0, null, null, null), null, []);

    private static ILookup<TKey, TValue> Empty<TKey, TValue>() => Array.Empty<KeyValuePair<TKey, TValue>>().ToLookup(p => p.Key, p => p.Value);

    public static double?[] Repeat(double? rating, int count) => Enumerable.Repeat(rating, count).ToArray();
}
