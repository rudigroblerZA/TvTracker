using System.Text.RegularExpressions;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Date">The holiday's next date (today when it's today), by the server's UTC calendar.</param>
public record HolidaySummaryDto(string Key, string Name, DateOnly Date, int DaysAway);

/// <param name="Episodes">Best known first, or, for given shows, by show then episode.</param>
/// <param name="SeasonShows">Shows made for the holiday as a whole ("Halloween Wars"), whose episodes aren't listed.</param>
public record HolidayDto(string Key, string Name, DateOnly Date, int DaysAway, IReadOnlyList<HolidayEpisodeDto> Episodes,
    IReadOnlyList<HolidayShowDto> SeasonShows);

/// <param name="InTitle">The holiday is in the episode's title, not only its summary.</param>
/// <param name="StandsAlone">
/// A comedy, animated, reality or game show, whose episodes usually work out of order; otherwise it's likely part of
/// a story, so watching it early can spoil.
/// </param>
public record HolidayEpisodeDto(Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber,
    int EpisodeNumber, string Title, DateTimeOffset? AirsAtUtc, int? RuntimeMinutes, double? Rating, bool InTitle, bool StandsAlone);

public record HolidayShowDto(Guid ShowId, string Title, string? PosterUrl);

public record HolidayShowsRequest(IReadOnlyList<Guid> ShowIds);

/// <summary>
/// Holiday episodes: the Halloween, Thanksgiving, Christmas, New Year's and Valentine's episodes of shows, found by the
/// holiday's words in episode titles and summaries.
/// <list type="bullet">
/// <item><b>Summaries need the season:</b> a title match counts whenever it aired, but a summary match only when it
/// aired near the holiday. Out of season, summaries mostly mention it in passing (The Middle's Valentine's episode
/// recalls a Halloween hayride).</item>
/// <item><b>Left out:</b> daily shows (a season of more than <see cref="DailySeasonEpisodes"/> episodes: morning shows,
/// soaps), whose dated episodes mention every holiday; and shows named after the holiday, every episode of which
/// matches, which are listed as <see cref="HolidayDto.SeasonShows"/> instead.</item>
/// <item><b>Cached:</b> the whole catalog's matches per holiday for 6 hours, so a request filters in memory.</item>
/// </list>
/// </summary>
public static class HolidayApi
{
    private const int DailySeasonEpisodes = 60;
    private const int MaxShows = 300;
    private const int MaxCatalogShows = 30;
    private const int PerShowInCatalog = 2;

    /// <param name="Pattern">Postgres regex (case-insensitive; <c>\m</c> starts a word).</param>
    /// <param name="Window">Month-day range in which a summary match counts; it may wrap the year.</param>
    private sealed record Holiday(string Key, string Name, string Pattern, (int Month, int Day) From, (int Month, int Day) To,
        Func<int, DateOnly> DateIn);

    private static readonly Holiday[] Holidays =
    [
        new("halloween", "Halloween", @"\m(halloween|hallowe'en|trick.or.treat|treehouse of horror|all hallows)", (10, 10), (11, 7),
            year => new DateOnly(year, 10, 31)),
        new("thanksgiving", "Thanksgiving", @"\m(thanksgiving|friendsgiving)", (11, 10), (12, 2),
            year => FourthThursdayOfNovember(year)),
        new("christmas", "Christmas and the holidays",
            @"\m(christmas|xmas|x-mas|hanukk?ah|chanukah|kwanzaa|festivus|santa claus|secret santa|yuletide|mistletoe)", (11, 25), (1, 3),
            year => new DateOnly(year, 12, 25)),
        new("new-year", "New Year's Eve", @"\m(new year'?s eve|new year'?s day|auld lang syne)", (12, 26), (1, 7),
            year => new DateOnly(year, 12, 31)),
        new("valentines", "Valentine's Day", @"\mvalentine", (2, 1), (2, 21),
            year => new DateOnly(year, 2, 14)),
    ];

    private static readonly HashSet<string> EpisodicKinds = ["reality", "animation", "game show", "variety", "documentary", "talk show"];

    private static readonly HashSet<string> StoryGenres = ["Drama", "Crime", "Thriller", "Mystery", "Science-Fiction", "Fantasy"];

    public static void MapHolidayApi(this RouteGroupBuilder api)
    {
        api.MapGet("/holidays", GetAll).RequireFeature(FeatureFlags.HolidayEpisodes);
        api.MapGet("/holidays/{key}", (string key, CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct) =>
            GetOneAsync(key, null, db, cache, time, ct)).RequireFeature(FeatureFlags.HolidayEpisodes);
        // POST, since a long follow list doesn't fit in a URL.
        api.MapPost("/holidays/{key}/shows", (string key, HolidayShowsRequest request, CatalogContext db, HybridCache cache, TimeProvider time,
            CancellationToken ct) => GetOneAsync(key, request.ShowIds ?? [], db, cache, time, ct)).RequireFeature(FeatureFlags.HolidayEpisodes);
    }

    /// <summary>Every holiday, the next one first.</summary>
    private static Ok<List<HolidaySummaryDto>> GetAll(TimeProvider time)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        return TypedResults.Ok(Holidays
            .Select(h => Next(h, today))
            .Select(x => new HolidaySummaryDto(x.Holiday.Key, x.Holiday.Name, x.Date, x.Date.DayNumber - today.DayNumber))
            .OrderBy(h => h.DaysAway)
            .ToList());
    }

    /// <param name="shows">Only these shows' episodes, all of them. Without, the best known across the catalog, at most
    /// <see cref="PerShowInCatalog"/> per show.</param>
    private static async Task<Results<Ok<HolidayDto>, NotFound>> GetOneAsync(
        string key, IReadOnlyList<Guid>? shows, CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct)
    {
        if (Holidays.FirstOrDefault(h => h.Key == key) is not { } holiday)
        {
            return TypedResults.NotFound();
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var date = Next(holiday, today).Date;
        var matches = await cache.GetOrCreateAsync($"holiday-episodes:{holiday.Key}",
            async token => await FindAsync(db, holiday, token),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(6), LocalCacheExpiration = TimeSpan.FromHours(1) },
            cancellationToken: ct);

        List<HolidayEpisodeDto> episodes;
        if (shows is not null)
        {
            var ids = shows.Take(MaxShows).ToHashSet();
            episodes = matches.Episodes.Where(e => ids.Contains(e.ShowId)).ToList();
        }
        else
        {
            // Matches are ordered by show popularity, then its best holiday episode; within a show, title matches and then the best rated.
            episodes = matches.Episodes
                .GroupBy(e => e.ShowId)
                .Take(MaxCatalogShows)
                .SelectMany(g => g.OrderByDescending(e => e.InTitle).ThenByDescending(e => e.Rating ?? 0).Take(PerShowInCatalog)
                    .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber))
                .ToList();
        }

        return TypedResults.Ok(new HolidayDto(holiday.Key, holiday.Name, date, date.DayNumber - today.DayNumber, episodes, matches.SeasonShows));
    }

    private sealed record Matches(List<HolidayEpisodeDto> Episodes, List<HolidayShowDto> SeasonShows);

    private static async Task<Matches> FindAsync(CatalogContext db, Holiday holiday, CancellationToken ct)
    {
        var pattern = holiday.Pattern;
        var candidates = await db.Episodes.AsNoTracking()
            .Where(e => Regex.IsMatch(e.Title, pattern, RegexOptions.IgnoreCase)
                || e.Overview != null && Regex.IsMatch(e.Overview, pattern, RegexOptions.IgnoreCase))
            .Select(e => new
            {
                e.Id, e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirsAtUtc, e.RuntimeMinutes, e.ExternalRating,
                InTitle = Regex.IsMatch(e.Title, pattern, RegexOptions.IgnoreCase)
            })
            .ToListAsync(ct);

        var showIds = candidates.Select(c => c.ShowId).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking()
            .Where(s => showIds.Contains(s.Id))
            .Select(s => new
            {
                s.Id, s.Title, s.PosterUrl, s.Popularity, s.Genres, s.Keywords,
                Named = Regex.IsMatch(s.Title, pattern, RegexOptions.IgnoreCase)
            })
            .ToDictionaryAsync(s => s.Id, ct);
        var daily = (await db.Episodes.AsNoTracking()
                .Where(e => showIds.Contains(e.ShowId))
                .GroupBy(e => new { e.ShowId, e.SeasonNumber })
                .Where(g => g.Count() > DailySeasonEpisodes)
                .Select(g => g.Key.ShowId)
                .ToListAsync(ct))
            .ToHashSet();

        var episodes = candidates
            .Where(c => shows.TryGetValue(c.ShowId, out var show) && !show.Named && !daily.Contains(c.ShowId))
            .Where(c => c.InTitle || c.AirsAtUtc is { } airs && InWindow(holiday, airs))
            .Select(c =>
            {
                var show = shows[c.ShowId];
                return (Popularity: show.Popularity, Dto: new HolidayEpisodeDto(c.ShowId, show.Title, show.PosterUrl, c.Id,
                    c.SeasonNumber, c.EpisodeNumber, c.Title, c.AirsAtUtc, c.RuntimeMinutes, c.ExternalRating, c.InTitle,
                    StandsAlone(show.Genres, show.Keywords)));
            })
            .ToList();
        // TVmaze's popularity tops out (a fifth of the catalog is at 99 or 100), so the show's best rated holiday
        // episode breaks ties.
        var best = episodes.GroupBy(x => x.Dto.ShowId).ToDictionary(g => g.Key, g => g.Max(x => x.Dto.Rating ?? 0));
        var ordered = episodes
            .OrderByDescending(x => x.Popularity)
            .ThenByDescending(x => best[x.Dto.ShowId])
            .ThenBy(x => x.Dto.ShowTitle)
            .ThenBy(x => x.Dto.SeasonNumber).ThenBy(x => x.Dto.EpisodeNumber)
            .Select(x => x.Dto)
            .ToList();

        var seasonShows = shows.Values
            .Where(s => s.Named && !daily.Contains(s.Id))
            .OrderByDescending(s => s.Popularity)
            .Select(s => new HolidayShowDto(s.Id, s.Title, s.PosterUrl))
            .ToList();
        return new Matches(ordered, seasonShows);
    }

    private static bool StandsAlone(List<string> genres, List<string> keywords) =>
        keywords.Any(EpisodicKinds.Contains) || genres.Contains("Comedy") && !genres.Any(StoryGenres.Contains);

    /// <summary>Whether it aired within the holiday's window, by its UTC date; the windows have a few days to spare.</summary>
    private static bool InWindow(Holiday holiday, DateTimeOffset airsAtUtc)
    {
        var day = (airsAtUtc.UtcDateTime.Month, airsAtUtc.UtcDateTime.Day);
        return holiday.From.CompareTo(holiday.To) <= 0
            ? day.CompareTo(holiday.From) >= 0 && day.CompareTo(holiday.To) <= 0
            : day.CompareTo(holiday.From) >= 0 || day.CompareTo(holiday.To) <= 0;
    }

    private static (Holiday Holiday, DateOnly Date) Next(Holiday holiday, DateOnly today)
    {
        var date = holiday.DateIn(today.Year);
        return (holiday, date >= today ? date : holiday.DateIn(today.Year + 1));
    }

    private static DateOnly FourthThursdayOfNovember(int year)
    {
        var first = new DateOnly(year, 11, 1);
        var offset = ((int)DayOfWeek.Thursday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 21);
    }
}
