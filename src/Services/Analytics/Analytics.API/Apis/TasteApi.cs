using System.Security.Claims;
using Analytics.Data;
using TvTracker;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

/// <param name="Bias">How the user's ratings compare with the provider's; null with fewer than three comparable ratings.</param>
/// <param name="Genres">Genres by the user's average stars for the shows they rated, best first.</param>
/// <param name="Months">Episodes and minutes watched in each of the last 12 months, oldest first.</param>
public record TasteDto(RatingBiasDto? Bias, IReadOnlyList<GenreTasteDto> Genres, IReadOnlyList<MonthWatchDto> Months, ProgressDto Progress);

/// <param name="Compared">Show and episode ratings that have a provider rating to compare with.</param>
/// <param name="AverageDifference">The user's rating (stars × 2, so 0-10) minus the provider's, averaged.</param>
/// <param name="Label">"kinder", "harsher" or "in line" (within <see cref="TasteApi.InLineMargin"/>).</param>
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
public static class TasteApi
{
    /// <summary>Average differences within this (0-10 scale) are "in line" with the provider.</summary>
    public const double InLineMargin = 0.5;

    private const int MinComparedRatings = 3;
    private const int MinRatedPerGenre = 2;

    /// <summary>A show rated this far (stars × 2 against 0-10) from the provider is listed as kinder or harsher.</summary>
    private const double ListedDifference = 1;
    private const int MaxListedShows = 3;
    private const int MaxGenres = 8;
    private const int MaxRecentlyDropped = 5;

    public static void MapTasteApi(this RouteGroupBuilder api) => api.MapGet("/me/taste", GetTasteAsync);

    /// <param name="tz">The caller's time zone (IANA id, from their profile), for the months; UTC when missing or unknown.</param>
    private static async Task<Ok<TasteDto>> GetTasteAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, string? tz = null, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var zone = tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found) ? found : TimeZoneInfo.Utc;

        var showRatings = await db.ShowRatings.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Join(db.Shows, r => r.ShowId, s => s.ShowId, (r, s) => new { r.Stars, Show = s })
            .ToListAsync(ct);
        var episodeComparisons = await db.EpisodeRatings.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Join(db.Episodes, r => r.EpisodeId, e => e.EpisodeId, (r, e) => new { r.Stars, e.ExternalRating })
            .Where(x => x.ExternalRating != null)
            .ToListAsync(ct);
        var watches = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .Join(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, e) => new { w.ShowId, w.WatchedAtUtc, e.RuntimeMinutes })
            .ToListAsync(ct);

        // Bias: stars × 2 against the provider's 0-10, over shows and episodes.
        var showComparisons = showRatings.Where(r => r.Show.ExternalRating is not null).ToList();
        var differences = showComparisons.Select(r => r.Stars * 2 - r.Show.ExternalRating!.Value)
            .Concat(episodeComparisons.Select(r => r.Stars * 2 - r.ExternalRating!.Value))
            .ToList();
        RatingBiasDto? bias = null;
        if (differences.Count >= MinComparedRatings)
        {
            var average = Math.Round(differences.Average(), 2);
            var byDifference = showComparisons
                .Select(r => (Dto: new BiasShowDto(r.Show.ShowId, r.Show.Title, r.Show.PosterUrl, r.Stars, r.Show.ExternalRating!.Value),
                    Difference: r.Stars * 2 - r.Show.ExternalRating!.Value))
                .ToList();
            bias = new RatingBiasDto(
                differences.Count,
                average,
                Math.Abs(average) < InLineMargin ? "in line" : average > 0 ? "kinder" : "harsher",
                byDifference.Where(d => d.Difference >= ListedDifference).OrderByDescending(d => d.Difference).Take(MaxListedShows).Select(d => d.Dto).ToList(),
                byDifference.Where(d => d.Difference <= -ListedDifference).OrderBy(d => d.Difference).Take(MaxListedShows).Select(d => d.Dto).ToList());
        }

        var genres = showRatings
            .SelectMany(r => r.Show.Genres.Select(g => (Genre: g, r.Stars, r.Show.ExternalRating)))
            .GroupBy(x => x.Genre)
            .Where(g => g.Count() >= MinRatedPerGenre)
            .Select(g => new GenreTasteDto(g.Key, g.Count(), Math.Round(g.Average(x => x.Stars), 2),
                g.Any(x => x.ExternalRating is not null) ? Math.Round(g.Where(x => x.ExternalRating is not null).Average(x => x.ExternalRating!.Value), 2) : null))
            .OrderByDescending(g => g.AverageStars)
            .ThenByDescending(g => g.Rated)
            .Take(MaxGenres)
            .ToList();

        // Months in the caller's calendar: a 9 pm watch on 31 January in New York is January there, February in UTC.
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var firstMonth = new DateTime(local.Year, local.Month, 1).AddMonths(-11);
        var byMonth = watches
            .Select(w => (Local: TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(w.WatchedAtUtc, DateTimeKind.Utc), zone), w.RuntimeMinutes))
            .Where(w => w.Local >= firstMonth)
            .GroupBy(w => (w.Local.Year, w.Local.Month))
            .ToDictionary(g => g.Key, g => (Episodes: g.Count(), Minutes: g.Sum(w => w.RuntimeMinutes ?? 0)));
        var months = Enumerable.Range(0, 12)
            .Select(i => firstMonth.AddMonths(i))
            .Select(m => byMonth.TryGetValue((m.Year, m.Month), out var v)
                ? new MonthWatchDto(m.Year, m.Month, v.Episodes, v.Minutes)
                : new MonthWatchDto(m.Year, m.Month, 0, 0))
            .ToList();

        return TypedResults.Ok(new TasteDto(bias, genres, months, await ProgressAsync(db, userId, watches.Select(w => (w.ShowId, w.WatchedAtUtc)).ToList(), now, ct)));
    }

    /// <summary>
    /// Finished, still watching or dropped, for every show the user has watched an episode of; regular episodes only
    /// (the viewer's aggregate), so a Christmas special never keeps a show from being finished.
    /// </summary>
    private static async Task<ProgressDto> ProgressAsync(
        AnalyticsContext db, string userId, List<(Guid ShowId, DateTime WatchedAtUtc)> watches, DateTimeOffset now, CancellationToken ct)
    {
        var showIds = watches.Select(w => w.ShowId).Distinct().ToList();
        var progress = await Viewer.ProgressAsync(db, userId, now, showIds, ct);
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

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
