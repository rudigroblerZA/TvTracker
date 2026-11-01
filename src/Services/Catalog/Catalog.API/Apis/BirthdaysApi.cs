using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Date">The day asked about, in the caller's calendar.</param>
/// <param name="People">Catalog actors born on that day of the year, best known first.</param>
public record BirthdaysDto(DateOnly Date, IReadOnlyList<BirthdayDto> People);

/// <param name="Turns">Their age on <see cref="BirthdaysDto.Date"/>: what they turn, or would have turned.</param>
/// <param name="Milestone">A round birthday (every ten years from 30) for the living.</param>
/// <param name="KnownFor">Their role in the most popular catalog show, highest billed.</param>
/// <param name="Shows">How many catalog shows they're in the main cast of.</param>
public record BirthdayDto(Guid PersonId, string Name, string? PhotoUrl, DateOnly Born, DateOnly? Died, int Turns, bool Milestone,
    BirthdayRoleDto KnownFor, int Shows);

public record BirthdayRoleDto(Guid ShowId, string Title, string? PosterUrl, string? Character);

/// <summary>
/// Birthdays: the catalog's actors born on a given day of the year, from TVmaze's birthdays (about 60% of people).
/// <list type="bullet">
/// <item><b>The caller's day:</b> the client sends its local date, since "today" in Sydney is yesterday in Los Angeles.
/// People born on 29 February are listed on 28 February in other years, as they'd celebrate.</item>
/// <item><b>Best known first:</b> Σ over their catalog roles of the show's popularity × 1 / (1 + billing order / 3), the
/// reunion radar's billing weight. TVmaze's popularity tops out (a fifth of the catalog at 99 or 100), so by popularity
/// alone a supporting player in a 100 show outranked the lead of a 98 one; billing and breadth of work break that.</item>
/// <item><b>Self roles out</b> (<c>Universes.IsSelfRole</c>): a host or a reality cast isn't known for a role.</item>
/// </list>
/// Everyone with a birthday is indexed once and cached <see cref="CacheFor"/>; a day is a filter over it.
/// </summary>
public static class BirthdaysApi
{
    public const int MaxPeople = 30;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private sealed record RoleRow(Guid PersonId, string Name, string? PhotoUrl, DateOnly Birthday, DateOnly? Deathday, Guid ShowId, string Title,
        string? PosterUrl, string? Character, int Order, double Popularity);

    private sealed record Person(Guid PersonId, string Name, string? PhotoUrl, DateOnly Born, DateOnly? Died, BirthdayRoleDto KnownFor, int Shows,
        double Fame);

    public static void MapBirthdaysApi(this RouteGroupBuilder api) =>
        api.MapGet("/birthdays", GetAsync).RequireFeature(FeatureFlags.Birthdays);

    private static async Task<Results<Ok<BirthdaysDto>, BadRequest<string>>> GetAsync(DateOnly? date, CatalogContext db, HybridCache cache,
        TimeProvider time, CancellationToken ct)
    {
        var day = date ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (day.Year is < 1900 or > 2200)
        {
            return TypedResults.BadRequest("A date between 1900 and 2200.");
        }

        var people = await LoadAsync(db, cache, ct);
        var leapDayToo = day is { Month: 2, Day: 28 } && !DateTime.IsLeapYear(day.Year);
        var born = people
            .Where(p => p.Born.Month == day.Month && p.Born.Day == day.Day || leapDayToo && p.Born is { Month: 2, Day: 29 })
            .Where(p => p.Born.Year < day.Year)
            .OrderByDescending(p => p.Fame).ThenBy(p => p.Name, StringComparer.Ordinal)
            .Take(MaxPeople)
            .Select(p =>
            {
                var turns = day.Year - p.Born.Year;
                return new BirthdayDto(p.PersonId, p.Name, p.PhotoUrl, p.Born, p.Died, turns, p.Died is null && turns >= 30 && turns % 10 == 0,
                    p.KnownFor, p.Shows);
            })
            .ToList();
        return TypedResults.Ok(new BirthdaysDto(day, born));
    }

    private static async Task<List<Person>> LoadAsync(CatalogContext db, HybridCache cache, CancellationToken ct) =>
        await cache.GetOrCreateAsync("birthdays", async token =>
        {
            var roles = await db.Cast.AsNoTracking()
                .Where(c => c.Person.Birthday != null)
                .Join(db.Shows, c => c.ShowId, s => s.Id, (c, s) => new RoleRow(c.PersonId, c.Person.Name, c.Person.PhotoUrl, c.Person.Birthday!.Value,
                    c.Person.Deathday, s.Id, s.Title, s.PosterUrl, c.Character, c.Order, s.Popularity))
                .ToListAsync(token);
            return roles
                .Where(r => r.Character is null || !Services.Universes.IsSelfRole(r.Character, r.Name))
                .GroupBy(r => r.PersonId)
                .Select(g =>
                {
                    var best = g.OrderByDescending(r => r.Popularity).ThenBy(r => r.Order).First();
                    return new Person(g.Key, best.Name, best.PhotoUrl, best.Birthday, best.Deathday,
                        new BirthdayRoleDto(best.ShowId, best.Title, best.PosterUrl, best.Character), g.Select(r => r.ShowId).Distinct().Count(),
                        g.Sum(r => r.Popularity / (1 + r.Order / 3.0)));
                })
                .ToList();
        }, new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);
}
