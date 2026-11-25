namespace TvTracker.InsightsEngine;

/// <param name="Bias">How the user's ratings compare with the provider's; null with fewer than three comparable ratings.</param>
/// <param name="Genres">Genres by the user's average stars for the shows they rated, best first.</param>
/// <param name="Months">Episodes and minutes watched in each of the last 12 months, oldest first.</param>
public record TasteDto(RatingBiasDto? Bias, IReadOnlyList<GenreTasteDto> Genres, IReadOnlyList<MonthWatchDto> Months, ProgressDto Progress);

/// <param name="Compared">Show and episode ratings that have a provider rating to compare with.</param>
/// <param name="AverageDifference">The user's rating (stars × 2, so 0-10) minus the provider's, averaged.</param>
/// <param name="Label">"kinder", "harsher" or "in line" (within <see cref="Taste.InLineMargin"/>).</param>
/// <param name="Kinder">Shows the user rated furthest above the provider.</param>
/// <param name="Harsher">Shows the user rated furthest below it.</param>
public record RatingBiasDto(int Compared, double AverageDifference, string Label, IReadOnlyList<BiasShowDto> Kinder, IReadOnlyList<BiasShowDto> Harsher);

public record BiasShowDto(Guid ShowId, string Title, string? PosterUrl, int Stars, double ExternalRating);

/// <param name="AverageExternal">The provider's average rating of the same shows, for comparison.</param>
public record GenreTasteDto(string Genre, int Rated, double AverageStars, double? AverageExternal);

public record MonthWatchDto(int Year, int Month, int Episodes, int Minutes);

/// <param name="Finished">Shows with every aired episode watched.</param>
/// <param name="Watching">Shows with episodes left, watched within <see cref="InsightRules.StoppedAfterDays"/> days.</param>
/// <param name="Dropped">Shows with episodes left and nothing watched for longer than that.</param>
public record ProgressDto(int Finished, int Watching, int Dropped, IReadOnlyList<ProgressShowDto> RecentlyDropped);

public record ProgressShowDto(Guid ShowId, string Title, string? PosterUrl, int Watched, int Aired, DateTime LastWatchedAtUtc);

/// <summary>
/// The signed-in user's taste, computed on request from their own ratings and watches only (so it's current and
/// needs no minimum counts: nobody else's data is involved). Complements Tracking's stats page.
/// </summary>
public static class Taste
{
    /// <summary>
    /// Average differences within this (0-10 scale) are "in line" with the provider.
    /// </summary>
    public const double InLineMargin = 0.5;

    private const int MinComparedRatings = 3;

    private const int MinRatedPerGenre = 2;

    /// <summary>
    /// A show rated this far (stars × 2 against 0-10) from the provider is listed as kinder or harsher.
    /// </summary>
    private const double ListedDifference = 1;

    private const int MaxListedShows = 3;

    private const int MaxGenres = 8;

    private const int MaxRecentlyDropped = 5;

    /// <summary>
    /// How the viewer's ratings compare with the provider's: stars × 2 against its 0-10, over shows and episodes; null
    /// with fewer than <see cref="MinComparedRatings"/> to compare.
    /// </summary>
    /// <param name="episodeDifferences">Each rated episode's stars × 2 less the provider's rating.</param>
    internal static RatingBiasDto? Bias(IReadOnlyCollection<(int Stars, ShowCopy Show)> showRatings, IEnumerable<double> episodeDifferences)
    {
        var byDifference = showRatings.Where(r => r.Show.ExternalRating is not null)
            .Select(r => (Dto: new BiasShowDto(r.Show.ShowId, r.Show.Title, r.Show.PosterUrl, r.Stars, r.Show.ExternalRating!.Value),
                Difference: r.Stars * 2 - r.Show.ExternalRating!.Value))
            .ToList();
        var differences = byDifference.Select(d => d.Difference).Concat(episodeDifferences).ToList();
        if (differences.Count < MinComparedRatings)
        {
            return null;
        }
        var average = Math.Round(differences.Average(), 2);
        return new RatingBiasDto(
            differences.Count,
            average,
            Math.Abs(average) < InLineMargin ? "in line" : average > 0 ? "kinder" : "harsher",
            byDifference.Where(d => d.Difference >= ListedDifference).OrderByDescending(d => d.Difference).Take(MaxListedShows).Select(d => d.Dto).ToList(),
            byDifference.Where(d => d.Difference <= -ListedDifference).OrderBy(d => d.Difference).Take(MaxListedShows).Select(d => d.Dto).ToList());
    }

    /// <summary>
    /// The viewer's stars by genre, for genres with <see cref="MinRatedPerGenre"/> or more rated shows, best liked first.
    /// </summary>
    internal static List<GenreTasteDto> Genres(IEnumerable<(int Stars, ShowCopy Show)> showRatings) => showRatings
        .SelectMany(r => r.Show.Genres.Select(g => (Genre: g, r.Stars, r.Show.ExternalRating)))
        .GroupBy(x => x.Genre)
        .Where(g => g.Count() >= MinRatedPerGenre)
        .Select(g => new GenreTasteDto(g.Key, g.Count(), Math.Round(g.Average(x => x.Stars), 2),
            g.Any(x => x.ExternalRating is not null) ? Math.Round(g.Where(x => x.ExternalRating is not null).Average(x => x.ExternalRating!.Value), 2) : null))
        .OrderByDescending(g => g.AverageStars)
        .ThenByDescending(g => g.Rated)
        .Take(MaxGenres)
        .ToList();

    /// <summary>
    /// Episodes and minutes for each of the twelve months up to this one, in the viewer's calendar: a 9 pm watch on 31
    /// January in New York is January there, February in UTC.
    /// </summary>
    /// <param name="watches">Each watch on the viewer's clock, with the episode's running time.</param>
    internal static List<MonthWatchDto> Months(IEnumerable<(DateTime Local, int? RuntimeMinutes)> watches, DateOnly today)
    {
        var firstMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
        var byMonth = watches
            .Where(w => w.Local >= firstMonth)
            .GroupBy(w => (w.Local.Year, w.Local.Month))
            .ToDictionary(g => g.Key, g => (Episodes: g.Count(), Minutes: g.Sum(w => w.RuntimeMinutes ?? 0)));
        return Enumerable.Range(0, 12)
            .Select(i => firstMonth.AddMonths(i))
            .Select(m => byMonth.TryGetValue((m.Year, m.Month), out var v)
                ? new MonthWatchDto(m.Year, m.Month, v.Episodes, v.Minutes)
                : new MonthWatchDto(m.Year, m.Month, 0, 0))
            .ToList();
    }

    /// <summary>
    /// The viewer's shows as finished, still being watched, or dropped (episodes left and nothing watched for
    /// <see cref="InsightRules.StoppedAfterDays"/> days), with the latest dropped.
    /// </summary>
    internal static ProgressDto Progress(IEnumerable<(Guid ShowId, DateTime WatchedAtUtc)> watches, IReadOnlyDictionary<Guid, ShowProgress> progress,
        IReadOnlyDictionary<Guid, ShowCopy> shows, DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime.AddDays(-InsightRules.StoppedAfterDays);
        var perShow = watches
            .GroupBy(w => w.ShowId)
            .Where(g => shows.ContainsKey(g.Key))
            .Select(g => (Show: shows[g.Key], Watched: progress.GetValueOrDefault(g.Key)?.Watched ?? 0, Aired: progress.GetValueOrDefault(g.Key)?.Aired ?? 0,
                Last: g.Max(w => w.WatchedAtUtc)))
            .ToList();

        var finished = perShow.Where(s => s.Aired > 0 && s.Watched >= s.Aired).ToList();
        var unfinished = perShow.Except(finished).ToList();
        var dropped = unfinished.Where(s => s.Last < cutoff).OrderByDescending(s => s.Last).ToList();

        return new ProgressDto(
            finished.Count,
            unfinished.Count - dropped.Count,
            dropped.Count,
            dropped.Take(MaxRecentlyDropped)
                .Select(s => new ProgressShowDto(s.Show.ShowId, s.Show.Title, s.Show.PosterUrl, s.Watched, s.Aired, s.Last))
                .ToList());
    }
}
