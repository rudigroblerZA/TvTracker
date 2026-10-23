using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <param name="Date">The week's middle day: today (every past year) or the date asked for (that year only).</param>
/// <param name="EveryYear">True for "this week in TV history", across every past year; false for one date's week.</param>
/// <param name="Premieres">Series premieres that week, best known first.</param>
/// <param name="Finales">Series finales of shows that have ended, best known first.</param>
/// <param name="BestEpisodes">Every year: the best-rated episode of each year, latest year first. One date: that week's best.</param>
/// <param name="OnAir">One date only: shows with an episode within a week either side, best known first.</param>
public record TimeMachineDto(
    DateOnly Date,
    bool EveryYear,
    IReadOnlyList<TimeMachineEpisodeDto> Premieres,
    IReadOnlyList<TimeMachineEpisodeDto> Finales,
    IReadOnlyList<TimeMachineEpisodeDto> BestEpisodes,
    IReadOnlyList<TimeMachineShowDto> OnAir);

/// <param name="Watched">Whether the caller has watched this episode.</param>
public record TimeMachineEpisodeDto(
    Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title,
    DateTimeOffset AiredAtUtc, double? Rating, bool Watched);

/// <param name="SeasonNumber">The season airing then.</param>
public record TimeMachineShowDto(Guid ShowId, string Title, string? PosterUrl, int SeasonNumber);

/// <summary>
/// The TV time machine: what premiered, ended and stood out in a week of TV history, from the catalog copy's air dates
/// and the provider's episode ratings. With no date, it's this week across every past year; with one, that date's week
/// (a birthday, say). Computed on request: one indexed scan of a week of episodes, or one week per past year.
/// </summary>
public static class TimeMachineApi
{
    /// <summary>Days either side of the date that count as its week.</summary>
    private const int HalfWeek = 3;

    private const int MaxMilestones = 12;
    private const int MaxBestEpisodes = 12;
    private const int MaxOnAir = 16;

    /// <summary>
    /// An episode rated more than this above its show is left out of the best episodes: thinly rated shows get a few
    /// devoted fans' 10s (an episode at 10 for a show at 7.4).
    /// </summary>
    private const double MaxAboveShow = 1.5;

    private static readonly DateOnly Earliest = new(1950, 1, 1);

    public static void MapTimeMachineApi(this RouteGroupBuilder api) =>
        api.MapGet("/insights/time-machine", GetAsync).RequireFeature(FeatureFlags.TimeMachine);

    /// <param name="date">A past date (yyyy-MM-dd) to travel to; leave out for this week in every past year.</param>
    private static async Task<Results<Ok<TimeMachineDto>, BadRequest<string>>> GetAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, DateOnly? date = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (date is { } asked && (asked < Earliest || asked >= today))
        {
            return TypedResults.BadRequest($"Pick a date from {Earliest:yyyy} up to yesterday.");
        }

        var everyYear = date is null;
        var middle = date ?? today;
        var days = Enumerable.Range(-HalfWeek, HalfWeek * 2 + 1).Select(d => middle.AddDays(d)).ToList();

        // Every year: the same days of the year, in years before this one. One date: just that week.
        var dayKeys = days.Select(d => d.Month * 100 + d.Day).ToList();
        var from = new DateTimeOffset(days[0].ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(days[^1].ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        var thisYear = today.Year;
        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => e.SeasonNumber > 0 && e.AirsAtUtc != null)
            .Where(e => everyYear
                ? e.AirsAtUtc!.Value.Year < thisYear && dayKeys.Contains(e.AirsAtUtc!.Value.Month * 100 + e.AirsAtUtc!.Value.Day)
                : e.AirsAtUtc >= from && e.AirsAtUtc <= to)
            .ToListAsync(ct);

        var showIds = episodes.Select(e => e.ShowId).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        // Each show's first and last regular episode, to tell premieres and finales.
        var spans = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc != null)
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, First = g.Min(e => e.AirsAtUtc), Last = g.Max(e => e.AirsAtUtc) })
            .ToDictionaryAsync(s => s.ShowId, ct);

        var watched = user.FindFirst("sub")?.Value is { } userId
            ? (await db.Watches.AsNoTracking()
                .Where(w => w.UserId == userId && showIds.Contains(w.ShowId))
                .Select(w => w.EpisodeId)
                .ToListAsync(ct)).ToHashSet()
            : [];

        // In air order, so when several episodes aired at once (a streaming drop), a premiere is the first of them and a
        // finale the last.
        var known = episodes.Where(e => shows.ContainsKey(e.ShowId)).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
        TimeMachineEpisodeDto Dto(EpisodeCopy e)
        {
            var show = shows[e.ShowId];
            return new TimeMachineEpisodeDto(show.ShowId, show.Title, show.PosterUrl, e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.Title,
                e.AirsAtUtc!.Value, e.ExternalRating, watched.Contains(e.EpisodeId));
        }
        IEnumerable<EpisodeCopy> BestKnownFirst(IEnumerable<EpisodeCopy> list) => list
            .OrderByDescending(e => shows[e.ShowId].Popularity)
            .ThenByDescending(e => shows[e.ShowId].ExternalRating ?? 0);

        var premieres = BestKnownFirst(known.Where(e => spans[e.ShowId].First == e.AirsAtUtc).DistinctBy(e => e.ShowId))
            .Take(MaxMilestones).Select(Dto).ToList();

        // A finale only once the show has ended, not one between seasons.
        var finales = BestKnownFirst(known.Where(e => shows[e.ShowId].Status == ShowStatus.Ended && spans[e.ShowId].Last == e.AirsAtUtc).Reverse().DistinctBy(e => e.ShowId))
            .Take(MaxMilestones).Select(Dto).ToList();

        var rated = known
            .Where(e => e.ExternalRating is { } r && shows[e.ShowId].ExternalRating is { } s && r - s <= MaxAboveShow)
            .OrderByDescending(e => e.ExternalRating)
            .ThenByDescending(e => shows[e.ShowId].Popularity)
            .ToList();
        var best = everyYear
            ? rated.GroupBy(e => e.AirsAtUtc!.Value.Year).OrderByDescending(g => g.Key).Select(g => g.First()).Take(MaxBestEpisodes)
            : rated.DistinctBy(e => e.ShowId).Take(MaxBestEpisodes);

        var onAir = new List<TimeMachineShowDto>();
        if (!everyYear)
        {
            var wideFrom = from.AddDays(-7 + HalfWeek);
            var wideTo = to.AddDays(7 - HalfWeek);
            var airing = await db.Episodes.AsNoTracking()
                .Where(e => e.SeasonNumber > 0 && e.AirsAtUtc >= wideFrom && e.AirsAtUtc <= wideTo)
                .GroupBy(e => e.ShowId)
                .Select(g => new { ShowId = g.Key, Season = g.Max(e => e.SeasonNumber) })
                .ToListAsync(ct);
            var airingIds = airing.Select(a => a.ShowId).ToList();
            var airingShows = await db.Shows.AsNoTracking().Where(s => airingIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
            onAir = airing
                .Where(a => airingShows.ContainsKey(a.ShowId))
                .Select(a => (Show: airingShows[a.ShowId], a.Season))
                .OrderByDescending(a => a.Show.Popularity)
                .ThenByDescending(a => a.Show.ExternalRating ?? 0)
                .Take(MaxOnAir)
                .Select(a => new TimeMachineShowDto(a.Show.ShowId, a.Show.Title, a.Show.PosterUrl, a.Season))
                .ToList();
        }

        return TypedResults.Ok(new TimeMachineDto(middle, everyYear, premieres, finales, best.Select(Dto).ToList(), onAir));
    }
}
