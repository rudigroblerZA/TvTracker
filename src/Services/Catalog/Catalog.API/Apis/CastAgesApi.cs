using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="FirstAired">The first regular episode's air date.</param>
/// <param name="LastAired">The latest regular episode to have aired (the finale, for an ended show).</param>
/// <param name="MedianAge">The cast's median age at <paramref name="LastAired"/>, from <see cref="CastAgesApi.MinAges"/> ages up.</param>
/// <param name="Members">Main cast members with a birthday, in billing order.</param>
public record CastAgesDto(Guid ShowId, DateOnly FirstAired, DateOnly LastAired, bool Ended, int? MedianAge, IReadOnlyList<CastAgeDto> Members);

/// <param name="AgeAtLatest">Their age at the latest episode, or at their death when that came first.</param>
/// <param name="AgeAtStart">Their age at the first episode; only given for <paramref name="GrewUp"/>, since the cast is
/// today's and a later arrival's age at the start says nothing.</param>
/// <param name="GrewUp">A child at the first episode and an adult by the latest, in a run of up to 15 years.</param>
/// <param name="DiedAt">When they died before the latest episode aired.</param>
public record CastAgeDto(Guid PersonId, string Name, string? Character, string? PhotoUrl, int AgeAtLatest, int? AgeAtStart, bool GrewUp,
    DateOnly? DiedAt);

/// <param name="GrewUp">Shows where two or more of the cast grew up on screen, most popular first.</param>
/// <param name="Youngest">Shows with the youngest casts at their latest episode.</param>
/// <param name="Oldest">Shows with the oldest casts at their latest episode.</param>
public record CastAgeListsDto(IReadOnlyList<CastAgeShowDto> GrewUp, IReadOnlyList<CastAgeShowDto> Youngest, IReadOnlyList<CastAgeShowDto> Oldest);

/// <param name="Members">For <see cref="CastAgeListsDto.GrewUp"/>, those who grew up; otherwise empty.</param>
public record CastAgeShowDto(Guid ShowId, string Title, string? PosterUrl, int FirstYear, int LastYear, int? MedianAge, IReadOnlyList<CastAgeDto> Members);

/// <summary>
/// Cast ages, from TVmaze's birthdays (on about 80% of main cast credits): each main cast member's age at the show's
/// latest regular episode, and the actors who grew up on screen.
/// <list type="bullet">
/// <item><b>Grew up on screen:</b> <see cref="ChildFrom"/> to <see cref="ChildUntil"/> at the first episode, 18 or over by
/// the latest, in a run of at most <see cref="MaxGrowingRun"/> years. TVmaze's cast is today's main cast, with no word on
/// when anyone joined, so a later arrival looks like a child at the premiere. The run cap rules out most of them (Law &amp;
/// Order's current cast "were" toddlers in 1990), but not all: Kaley Cuoco joined Charmed in its last season. The list
/// therefore needs two such actors in a show, and the pages say a later arrival can appear.</item>
/// <item><b>Age at the latest episode</b> is right whenever they joined, so it's what the show page leads with, and what the
/// youngest and oldest casts are ranked by (a median, from <see cref="MinAges"/> ages).</item>
/// <item><b>Lists</b> are scripted shows of <see cref="ListMinPopularity"/>+ popularity, as Between seasons.</item>
/// </list>
/// Computed together and cached <see cref="CacheFor"/>; a show page reads its row from that.
/// </summary>
public static class CastAgesApi
{
    public const int ChildFrom = 6;
    public const int ChildUntil = 15;
    public const int MaxGrowingRun = 15;
    public const int MinAges = 4;
    public const double ListMinPopularity = 90;
    private const int ListSize = 25;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private sealed record RunRow(Guid ShowId, DateTimeOffset First, DateTimeOffset Last);

    private sealed record MemberRow(Guid ShowId, Guid PersonId, string Name, string? Character, string? PhotoUrl, DateOnly Birthday,
        DateOnly? Deathday, int Order);

    private sealed record ShowRow(Guid Id, string Title, string? PosterUrl, double Popularity, bool Scripted, bool Ended);

    private sealed record AllAges(List<CastAgesDto> Shows, List<ShowRow> Titles);

    public static void MapCastAgesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/cast-ages", GetShowAsync).RequireFeature(FeatureFlags.CastAges);
        api.MapGet("/cast-ages", GetListsAsync).RequireFeature(FeatureFlags.CastAges);
    }

    private static async Task<Results<Ok<CastAgesDto>, NotFound>> GetShowAsync(Guid id, CatalogContext db, HybridCache cache, TimeProvider time,
        CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, time, ct);
        return all.Shows.FirstOrDefault(s => s.ShowId == id) is { } show ? TypedResults.Ok(show) : TypedResults.NotFound();
    }

    private static async Task<Ok<CastAgeListsDto>> GetListsAsync(CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct)
    {
        var all = await LoadAsync(db, cache, time, ct);
        var titles = all.Titles.Where(t => t.Scripted && t.Popularity >= ListMinPopularity).ToDictionary(t => t.Id);
        var listed = all.Shows.Where(s => titles.ContainsKey(s.ShowId)).ToList();
        CastAgeShowDto Row(CastAgesDto s, bool members) => new(s.ShowId, titles[s.ShowId].Title, titles[s.ShowId].PosterUrl, s.FirstAired.Year,
            s.LastAired.Year, s.MedianAge, members ? s.Members.Where(m => m.GrewUp).ToList() : []);

        var aged = listed.Where(s => s.MedianAge is not null).ToList();
        return TypedResults.Ok(new CastAgeListsDto(
            listed.Where(s => s.Members.Count(m => m.GrewUp) >= 2)
                .OrderByDescending(s => titles[s.ShowId].Popularity).ThenByDescending(s => s.Members.Count(m => m.GrewUp))
                .Take(ListSize).Select(s => Row(s, true)).ToList(),
            aged.OrderBy(s => s.MedianAge).ThenByDescending(s => titles[s.ShowId].Popularity).Take(ListSize).Select(s => Row(s, false)).ToList(),
            aged.OrderByDescending(s => s.MedianAge).ThenByDescending(s => titles[s.ShowId].Popularity).Take(ListSize).Select(s => Row(s, false)).ToList()));
    }

    private static async Task<AllAges> LoadAsync(CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct) =>
        await cache.GetOrCreateAsync("cast-ages", async token =>
        {
            var now = time.GetUtcNow();
            var runs = await db.Database.SqlQuery<RunRow>($"""
                SELECT "ShowId", MIN("AirsAtUtc") AS "First", MAX("AirsAtUtc") AS "Last"
                FROM episodes
                WHERE "SeasonNumber" > 0 AND "AirsAtUtc" <= {now}
                GROUP BY "ShowId"
                """).ToDictionaryAsync(r => r.ShowId, token);
            var members = await db.Cast.AsNoTracking()
                .Where(c => c.Person.Birthday != null)
                .Select(c => new MemberRow(c.ShowId, c.PersonId, c.Person.Name, c.Character, c.Person.PhotoUrl, c.Person.Birthday!.Value,
                    c.Person.Deathday, c.Order))
                .ToListAsync(token);
            var titles = await db.Shows.AsNoTracking()
                .Select(s => new ShowRow(s.Id, s.Title, s.PosterUrl, s.Popularity, s.Keywords.Contains("scripted"),
                    s.Status == ShowStatus.Ended || s.Status == ShowStatus.Cancelled))
                .ToListAsync(token);
            var ended = titles.Where(t => t.Ended).Select(t => t.Id).ToHashSet();
            var shows = members.GroupBy(m => m.ShowId)
                .Where(g => runs.ContainsKey(g.Key))
                .Select(g => Build(g.Key, runs[g.Key], ended.Contains(g.Key), g.OrderBy(m => m.Order).ToList()))
                .ToList();
            var ids = shows.Select(s => s.ShowId).ToHashSet();
            return new AllAges(shows, titles.Where(t => ids.Contains(t.Id)).ToList());
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);

    private static CastAgesDto Build(Guid showId, RunRow run, bool ended, List<MemberRow> members)
    {
        var first = DateOnly.FromDateTime(run.First.UtcDateTime);
        var last = DateOnly.FromDateTime(run.Last.UtcDateTime);
        var growingRun = Age(first, last) <= MaxGrowingRun;
        var dtos = members
            .Where(m => Age(m.Birthday, last) >= 1) // a birthday after the show began is a data error
            .Select(m =>
            {
                var died = m.Deathday is { } d && d < last ? d : (DateOnly?)null;
                var atLatest = Age(m.Birthday, died ?? last);
                var atStart = Age(m.Birthday, first);
                var grewUp = growingRun && died is null && atStart is >= ChildFrom and <= ChildUntil && atLatest >= 18;
                return new CastAgeDto(m.PersonId, m.Name, m.Character, m.PhotoUrl, atLatest, grewUp ? atStart : null, grewUp, died);
            })
            .ToList();
        // The median of the living cast at the latest episode: someone who died years before it would pull it down.
        var ages = dtos.Where(d => d.DiedAt is null).Select(d => d.AgeAtLatest).Order().ToList();
        int? median = ages.Count >= MinAges
            ? ages.Count % 2 == 1 ? ages[ages.Count / 2] : (ages[ages.Count / 2 - 1] + ages[ages.Count / 2]) / 2
            : null;
        return new CastAgesDto(showId, first, last, ended, median, dtos);
    }

    /// <summary>Whole years from <paramref name="from"/> to <paramref name="to"/>, as a birthday counts them.</summary>
    private static int Age(DateOnly from, DateOnly to) =>
        to.Year - from.Year - (to.Month < from.Month || to.Month == from.Month && to.Day < from.Day ? 1 : 0);
}
