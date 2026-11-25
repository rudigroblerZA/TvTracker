namespace TvTracker.InsightsEngine;

/// <param name="Years">Every year of the viewer's life with something in it, oldest first.</param>
public record TvLifeDto(int Born, IReadOnlyList<TvLifeYearDto> Years, TvLifeFactsDto Facts);

/// <param name="Age">How old the viewer was that year (0 in the year they were born).</param>
/// <param name="Premieres">The year's biggest premieres in the catalog, most popular first.</param>
/// <param name="Finales">Shows that ended that year, most popular first.</param>
/// <param name="Yours">What the viewer watched that year; null for a year with nothing watched.</param>
public record TvLifeYearDto(int Year, int Age, IReadOnlyList<TvLifeShowDto> Premieres, IReadOnlyList<TvLifeShowDto> Finales, TvLifeYourYearDto? Yours);

/// <param name="Watched">The viewer has watched some of it.</param>
public record TvLifeShowDto(Guid ShowId, string Title, string? PosterUrl, double? Rating, bool Watched);

/// <param name="Started">Shows the viewer watched the first episode of that year.</param>
/// <param name="Finished">Shows the viewer finished that year (every aired regular episode, the last of them that year).</param>
/// <param name="TopShow">The show they watched most that year.</param>
public record TvLifeYourYearDto(int Started, int Finished, int Minutes, TvLifeShowDto? TopShow);

/// <param name="OlderThanYou">Shows the viewer has watched that premiered before they were born.</param>
/// <param name="Oldest">The earliest-premiering show they've watched, with its year.</param>
/// <param name="BornWith">The most popular show that premiered the year they were born.</param>
/// <param name="BiggestYear">The year they watched most, by minutes.</param>
public record TvLifeFactsDto(int OlderThanYou, TvLifeShowDto? Oldest, int? OldestYear, TvLifeShowDto? BornWith, int? BiggestYear,
    int TotalMinutes, int ShowsWatched);

/// <summary>
/// Your TV life: the viewer's years, from the one they were born in, each with the biggest premieres and finales of
/// the catalog and what the viewer themselves watched that year. The birth year is the viewer's to give (kept on the
/// device by the page, not in any profile): nothing here is about anyone else, so the privacy rule doesn't apply.
/// Premieres and finales are read from the catalog copy's regular episodes (the copy keeps no premiere date), so a
/// show counts for the year its first regular episode aired and, once ended, the year its last one did.
/// </summary>
public static class TvLife
{
    public const int EarliestBirthYear = 1920;

    private const int PremieresPerYear = 3;

    private const int FinalesPerYear = 2;

    /// <summary>
    /// A show's first and last aired regular episode.
    /// </summary>
    internal sealed record Run(Guid ShowId, DateTimeOffset First, DateTimeOffset Last);

    /// <summary>
    /// The viewer's life in TV, year by year from <paramref name="born"/> to <paramref name="thisYear"/>.
    /// </summary>
    /// <param name="runs">Every catalog show's run.</param>
    /// <param name="watches">The viewer's watches by the year they fall in (their calendar), bulk marks left out.</param>
    /// <param name="watchedShows">Every show they have a watch on, bulk marks included.</param>
    /// <param name="finished">The shows they finished, by the year of the last watch.</param>
    internal static TvLifeDto Of(int born, int thisYear, IReadOnlyList<Run> runs, IReadOnlyDictionary<Guid, ShowCopy> shows,
        IReadOnlyList<(Guid ShowId, int Year, int Minutes)> watches, IReadOnlySet<Guid> watchedShows, ILookup<int, Guid> finished)
    {
        TvLifeShowDto Dto(Guid id) => new(id, shows[id].Title, shows[id].PosterUrl, shows[id].ExternalRating, watchedShows.Contains(id));
        var known = runs.Where(r => shows.ContainsKey(r.ShowId)).ToList();
        var premieres = known.ToLookup(r => r.First.Year, r => r.ShowId);
        var finales = known.Where(r => shows[r.ShowId].HasEnded()).ToLookup(r => r.Last.Year, r => r.ShowId);
        var started = watches.GroupBy(w => w.ShowId).ToLookup(g => g.Min(w => w.Year), g => g.Key);
        var minutesByYear = watches.GroupBy(w => w.Year).ToDictionary(g => g.Key, g => g.Sum(w => w.Minutes));

        IReadOnlyList<TvLifeShowDto> Top(IEnumerable<Guid> ids, int take) =>
            ids.OrderByDescending(id => shows[id].Popularity).ThenByDescending(id => shows[id].ExternalRating ?? 0).Take(take).Select(Dto).ToList();

        var years = new List<TvLifeYearDto>();
        for (var year = born; year <= thisYear; year++)
        {
            var yearWatches = watches.Where(w => w.Year == year).ToList();
            var yours = yearWatches.Count == 0 ? null : new TvLifeYourYearDto(
                started[year].Count(), finished[year].Count(), minutesByYear.GetValueOrDefault(year),
                yearWatches.GroupBy(w => w.ShowId).Where(g => shows.ContainsKey(g.Key)).OrderByDescending(g => g.Sum(w => w.Minutes))
                    .Select(g => Dto(g.Key)).FirstOrDefault());
            var yearPremieres = Top(premieres[year], PremieresPerYear);
            var yearFinales = Top(finales[year], FinalesPerYear);
            if (yours is null && yearPremieres.Count == 0 && yearFinales.Count == 0)
            {
                continue;
            }
            years.Add(new TvLifeYearDto(year, year - born, yearPremieres, yearFinales, yours));
        }

        var older = known.Where(r => watchedShows.Contains(r.ShowId) && r.First.Year < born).ToList();
        var oldest = known.Where(r => watchedShows.Contains(r.ShowId)).MinBy(r => r.First);
        var facts = new TvLifeFactsDto(
            older.Count,
            oldest is null ? null : Dto(oldest.ShowId),
            oldest?.First.Year,
            Top(premieres[born], 1).FirstOrDefault(),
            minutesByYear.Count == 0 ? null : minutesByYear.MaxBy(m => m.Value).Key,
            watches.Sum(w => w.Minutes),
            watchedShows.Count(shows.ContainsKey));
        return new TvLifeDto(born, years, facts);
    }
}
