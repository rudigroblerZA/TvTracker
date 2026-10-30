using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="Since">The date the roles are counted from: the show's last aired episode when it has ended, else its premiere.</param>
/// <param name="WentOn">Cast members with at least one main role since.</param>
/// <param name="CreditsPending">Cast members whose provider credits haven't been fetched yet, so their roles are unknown.</param>
/// <param name="Biggest">The most popular role any of them took since.</param>
/// <param name="Cast">In billing order; members with no roles since are listed with none.</param>
public record ShowAfterwardsDto(Guid ShowId, bool Ended, DateOnly Since, int CastCount, int WentOn, int CreditsPending, BiggestRoleDto? Biggest,
    IReadOnlyList<CastAfterwardsDto> Cast);

public record BiggestRoleDto(Guid PersonId, string Name, NextRoleDto Role);

/// <param name="CreditsFetched">The provider's credits for this person are in the catalog; without them nothing is known.</param>
/// <param name="Roles">Main roles since, earliest first, at most <see cref="AfterwardsApi.RolesPerPerson"/>.</param>
public record CastAfterwardsDto(Guid PersonId, string Name, string? PhotoUrl, string? Character, bool CreditsFetched, IReadOnlyList<NextRoleDto> Roles);

/// <param name="ShowId">Null for a show that isn't in the catalog; the UI offers a search, which imports it.</param>
public record NextRoleDto(Guid? ShowId, string ExternalId, string Title, string? PosterUrl, int? Year, ShowStatus Status, string? Character,
    double Popularity, double? Rating);

/// <summary>
/// Where the cast went next: for a show that has ended, each main cast member's main roles in shows that premiered
/// after its finale; for a running show, the roles they've taken since it started. From the provider's cast credits
/// (<see cref="PersonCastCredit"/>), so shows outside the catalog count, each marked so the page can offer to import
/// it. Main casts only, as TVmaze lists them: a guest arc or a film doesn't appear. Self roles ("Himself") are left out,
/// since a talk-show appearance isn't a role. Cached an hour per show.
/// </summary>
public static class AfterwardsApi
{
    public const int RolesPerPerson = 3;
    private const int MaxCast = 20;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    public static void MapAfterwardsApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/{id:guid}/afterwards", GetAsync).RequireFeature(FeatureFlags.CastAfterwards);

    private static async Task<Results<Ok<ShowAfterwardsDto>, NotFound>> GetAsync(Guid id, CatalogContext db, HybridCache cache, TimeProvider time,
        CancellationToken ct)
    {
        var result = await cache.GetOrCreateAsync($"afterwards:{id}", async token => await BuildAsync(db, id, time.GetUtcNow(), token),
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }

    private static async Task<ShowAfterwardsDto?> BuildAsync(CatalogContext db, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var show = await db.Shows.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new { s.Id, s.ExternalId, s.Status, s.FirstAirDate })
            .SingleOrDefaultAsync(ct);
        if (show is null)
        {
            return null;
        }

        var ended = show.Status is ShowStatus.Ended or ShowStatus.Cancelled;
        var lastAired = ended
            ? await db.Episodes.AsNoTracking().Where(e => e.ShowId == id && e.SeasonNumber > 0 && e.AirsAtUtc <= now).MaxAsync(e => e.AirsAtUtc, ct)
            : null;
        var since = lastAired is { } last ? DateOnly.FromDateTime(last.UtcDateTime) : show.FirstAirDate ?? DateOnly.FromDateTime(now.UtcDateTime);

        var cast = await db.Cast.AsNoTracking().Where(c => c.ShowId == id)
            .OrderBy(c => c.Order).Take(MaxCast)
            .Select(c => new { c.PersonId, c.Person.Name, c.Person.PhotoUrl, c.Character, Fetched = c.Person.CastCreditsRefreshedAtUtc != null })
            .ToListAsync(ct);
        var personIds = cast.Select(c => c.PersonId).ToList();
        var credits = (await db.CastCredits.AsNoTracking()
                .Where(c => personIds.Contains(c.PersonId) && c.ShowExternalId != show.ExternalId && c.Premiered >= since)
                .ToListAsync(ct))
            .ToLookup(c => c.PersonId);

        var members = cast.Select(c => new CastAfterwardsDto(c.PersonId, c.Name, c.PhotoUrl, c.Character, c.Fetched,
                credits[c.PersonId]
                    .Where(r => r.Character is null || !Universes.IsSelfRole(r.Character, c.Name))
                    .OrderBy(r => r.Premiered)
                    .Take(RolesPerPerson)
                    .Select(r => new NextRoleDto(r.ShowId, r.ShowExternalId, r.Title, r.PosterUrl, r.Premiered?.Year, r.Status, r.Character, r.Popularity, r.Rating))
                    .ToList()))
            .ToList();

        var biggest = members.SelectMany(m => m.Roles.Select(r => (Member: m, Role: r)))
            .OrderByDescending(x => x.Role.Popularity).ThenByDescending(x => x.Role.Rating ?? 0)
            .Select(x => new BiggestRoleDto(x.Member.PersonId, x.Member.Name, x.Role))
            .FirstOrDefault();
        return new ShowAfterwardsDto(id, ended, since, members.Count, members.Count(m => m.Roles.Count > 0), members.Count(m => !m.CreditsFetched),
            biggest, members);
    }
}
