using TvTracker.IntegrationEvents;

namespace TvTracker.InsightsEngine;

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
/// and the provider's episode ratings. With no date, it's this week in every past year; with one, that date's week (a
/// birthday, say). Weeks are calendar days in the caller's time zone, so an evening premiere in New York counts on its
/// own day.
/// </summary>
public static class TimeMachine
{
    /// <summary>
    /// Days either side of the date that count as its week.
    /// </summary>
    internal const int HalfWeek = 3;

    /// <summary>
    /// One date's "on air then": shows with an episode within this many days either side.
    /// </summary>
    internal const int OnAirDays = 7;

    private const int MaxMilestones = 12;

    private const int MaxBestEpisodes = 12;

    private const int MaxOnAir = 16;

    /// <summary>
    /// An episode rated more than this above its show is left out of the best episodes: thinly rated shows get a few
    /// devoted fans' 10s (an episode at 10 for a show at 7.4).
    /// </summary>
    private const double MaxAboveShow = 1.5;

    internal static readonly DateOnly Earliest = new(1950, 1, 1);

    /// <summary>
    /// The weeks' middle days: the date asked for, or (none) the same day in every past year, its week taken whole. Near
    /// New Year that week spans two years (29 December to 4 January), and the latest one is still a year back.
    /// </summary>
    internal static List<DateOnly> Middles(DateOnly today, DateOnly? date) => date is { } asked
        ? [asked]
        : Enumerable.Range(Earliest.Year, today.Year - Earliest.Year).Select(year => SameDay(today, year)).ToList();

    /// <summary>
    /// What premiered, ended and stood out in the weeks around <paramref name="middles"/>.
    /// </summary>
    /// <param name="date">The date asked for; null for this week in every past year.</param>
    /// <param name="episodes">The regular episodes that aired in those weeks.</param>
    /// <param name="shows">Their shows; an episode of a show that's gone is left out.</param>
    /// <param name="spans">Each show's first and last regular episode's air time, to tell premieres and finales.</param>
    /// <param name="airing">One date only: the episodes within <see cref="OnAirDays"/> of it, with their shows.</param>
    internal static TimeMachineDto Of(DateOnly? date, DateOnly today, TimeZoneInfo zone, List<DateOnly> middles, List<EpisodeCopy> episodes,
        IReadOnlyDictionary<Guid, ShowCopy> shows, IReadOnlyDictionary<Guid, (DateTimeOffset? First, DateTimeOffset? Last)> spans,
        (List<EpisodeCopy> Episodes, IReadOnlyDictionary<Guid, ShowCopy> Shows)? airing = null)
    {
        var everyYear = date is null;

        // By episode number, so when several episodes aired at once (a streaming drop) the premiere is the first of them
        // and the finale the last; which episodes those are comes from the air-time spans.
        var known = episodes.Where(e => shows.ContainsKey(e.ShowId)).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
        TimeMachineEpisodeDto Dto(EpisodeCopy e)
        {
            var show = shows[e.ShowId];
            return new TimeMachineEpisodeDto(show.ShowId, show.Title, show.PosterUrl, e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.Title,
                e.AirsAtUtc!.Value, e.ExternalRating, false);
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

        // Every year: one per year's week (by the week's middle day, as a week can span New Year), latest first.
        var best = everyYear
            ? rated.GroupBy(e => WeekOf(e, zone, middles)).OrderByDescending(g => g.Key).Select(g => g.First()).Take(MaxBestEpisodes)
            : rated.DistinctBy(e => e.ShowId).Take(MaxBestEpisodes);

        return new TimeMachineDto(date ?? today, everyYear, premieres, finales, best.Select(Dto).ToList(),
            airing is { } on ? OnAir(on.Episodes, on.Shows) : []);
    }

    /// <summary>
    /// Shows with an episode among <paramref name="airing"/> (those within <see cref="OnAirDays"/> of the date), each at
    /// its latest season then, best known first.
    /// </summary>
    private static List<TimeMachineShowDto> OnAir(List<EpisodeCopy> airing, IReadOnlyDictionary<Guid, ShowCopy> shows) =>
        airing.GroupBy(e => e.ShowId)
            .Where(g => shows.ContainsKey(g.Key))
            .Select(g => (Show: shows[g.Key], Season: g.Max(e => e.SeasonNumber)))
            .OrderByDescending(a => a.Show.Popularity)
            .ThenByDescending(a => a.Show.ExternalRating ?? 0)
            .Take(MaxOnAir)
            .Select(a => new TimeMachineShowDto(a.Show.ShowId, a.Show.Title, a.Show.PosterUrl, a.Season))
            .ToList();

    /// <summary>
    /// The same month and day in <paramref name="year"/>; 29 February becomes the 28th in other years.
    /// </summary>
    private static DateOnly SameDay(DateOnly day, int year) => new(year, day.Month, Math.Min(day.Day, DateTime.DaysInMonth(year, day.Month)));

    /// <summary>
    /// Midnight starting <paramref name="day"/> in <paramref name="zone"/>, as UTC (or the first valid time after it).
    /// </summary>
    internal static DateTime StartOfDayUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>
    /// The middle day of the week an episode fell in.
    /// </summary>
    private static DateOnly WeekOf(EpisodeCopy episode, TimeZoneInfo zone, List<DateOnly> middles)
    {
        var aired = ViewerRules.LocalDate(episode.AirsAtUtc!.Value, zone);
        return middles.MinBy(m => Math.Abs(m.DayNumber - aired.DayNumber));
    }
}
