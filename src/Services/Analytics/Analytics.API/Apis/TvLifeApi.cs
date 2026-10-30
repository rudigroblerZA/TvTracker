using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

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
public static class TvLifeApi
{
    public const int EarliestBirthYear = 1920;
    private const int PremieresPerYear = 3;
    private const int FinalesPerYear = 2;

    public static void MapTvLifeApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/tv-life", GetAsync).RequireFeature(FeatureFlags.TvLife);

    /// <param name="born">The viewer's birth year.</param>
    /// <param name="tz">The caller's time zone (IANA id, from their profile); UTC when missing or unknown.</param>
    private static async Task<Results<Ok<TvLifeDto>, ValidationProblem>> GetAsync(
        int born, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, string? tz = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (born < EarliestBirthYear || born > now.Year)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["born"] = [$"Use a year from {EarliestBirthYear} to {now.Year}."] });
        }
        var userId = user.GetUserId();
        var zone = tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found) ? found : TimeZoneInfo.Utc;

        // Every catalog show's first and last regular episode, from one aggregate.
        var runs = await db.Episodes.AsNoTracking().AiredRegular(now)
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, First = g.Min(e => e.AirsAtUtc)!.Value, Last = g.Max(e => e.AirsAtUtc)!.Value })
            .ToListAsync(ct);
        var shows = await db.Shows.AsNoTracking().ToDictionaryAsync(s => s.ShowId, ct);

        // The viewer's watches in their calendar, bulk marks left out of minutes and firsts (they say when a back catalog was marked).
        var rows = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .Join(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, e) => new { w.ShowId, w.WatchedAtUtc, Minutes = e.RuntimeMinutes ?? 0 })
            .ToListAsync(ct);
        var bulk = rows.GroupBy(r => (r.ShowId, r.WatchedAtUtc)).Where(g => g.Count() >= CopyRules.BulkMark).Select(g => g.Key).ToHashSet();
        var watches = rows.Where(r => !bulk.Contains((r.ShowId, r.WatchedAtUtc)))
            .Select(r => (r.ShowId, Year: TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.WatchedAtUtc, DateTimeKind.Utc), zone).Year, r.Minutes))
            .ToList();
        var watchedShows = rows.Select(r => r.ShowId).ToHashSet();
        var progress = await Viewer.ProgressAsync(db, userId, now, null, ct);

        TvLifeShowDto Dto(Guid id) => new(id, shows[id].Title, shows[id].PosterUrl, shows[id].ExternalRating, watchedShows.Contains(id));
        var known = runs.Where(r => shows.ContainsKey(r.ShowId)).ToList();
        var premieres = known.ToLookup(r => r.First.Year, r => r.ShowId);
        var finales = known.Where(r => shows[r.ShowId].HasEnded()).ToLookup(r => r.Last.Year, r => r.ShowId);
        var started = watches.GroupBy(w => w.ShowId).ToLookup(g => g.Min(w => w.Year), g => g.Key);
        var finished = progress.Values.Where(p => p.AllWatched && p.LastWatchedAtUtc is { } last)
            .ToLookup(p => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(p.LastWatchedAtUtc!.Value, DateTimeKind.Utc), zone).Year, p => p.ShowId);
        var minutesByYear = watches.GroupBy(w => w.Year).ToDictionary(g => g.Key, g => g.Sum(w => w.Minutes));

        IReadOnlyList<TvLifeShowDto> Top(IEnumerable<Guid> ids, int take) =>
            ids.OrderByDescending(id => shows[id].Popularity).ThenByDescending(id => shows[id].ExternalRating ?? 0).Take(take).Select(Dto).ToList();

        var years = new List<TvLifeYearDto>();
        for (var year = born; year <= now.Year; year++)
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
        return TypedResults.Ok(new TvLifeDto(born, years, facts));
    }
}
