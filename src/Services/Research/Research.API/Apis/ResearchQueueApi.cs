using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Research.API.Data;
using Research.API.Services;
using TvTracker;

namespace Research.API.Apis;

/// <param name="Rank">The show's place among the catalog's most popular, 1 first.</param>
/// <param name="Started">A moderator has gathered or drafted for it, but not saved.</param>
public record QueuedShowDto(Guid ShowId, string Title, string? PosterUrl, int Rank, string? Network, int? Year, bool Started);

/// <param name="Field">"network", "premiered" or "episodes".</param>
public record DisagreementDto(string Field, string? Catalog, string Wikidata);

/// <param name="Researched">It has saved research; saving a picked value overrides the catalog's.</param>
public record DisagreeingShowDto(Guid ShowId, string Title, int Rank, bool Researched, string WikidataUrl, IReadOnlyList<DisagreementDto> Fields);

/// <param name="CheckedAtUtc">When the shows were last checked against Wikidata; null before the first check.</param>
public record ResearchQueueDto(IReadOnlyList<QueuedShowDto> Unresearched, IReadOnlyList<DisagreeingShowDto> Disagreements, DateTime? CheckedAtUtc);

/// <summary>
/// What to research next, for moderators: the catalog's most popular shows with no saved research, and popular shows
/// where Wikidata disagrees with the catalog (see <see cref="CatalogChecks"/>). Anyone else gets 404, like the rest of
/// research.
/// </summary>
public static class ResearchQueueApi
{
    private const int MaxUnresearched = 30;

    public static void MapResearchQueueApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/research/queue", GetQueueAsync).RequireAuthorization().RequireFeature(FeatureFlags.Research);

    private static async Task<Results<Ok<ResearchQueueDto>, NotFound>> GetQueueAsync(
        ClaimsPrincipal user, Researchers researchers, ResearchContext db, CatalogReader catalog, CancellationToken ct)
    {
        if (!researchers.Allowed(user))
        {
            return TypedResults.NotFound();
        }

        var saved = (await db.Saved.Select(s => s.ShowId).Distinct().ToListAsync(ct)).ToHashSet();
        var started = (await db.Dossiers.Select(d => d.ShowId).ToListAsync(ct)).ToHashSet();
        var popular = await catalog.GetPopularFactsAsync(CatalogChecks.Popular, ct);
        var unresearched = popular
            .Select((s, i) => (Show: s, Rank: i + 1))
            .Where(x => !saved.Contains(x.Show.Id))
            .Take(MaxUnresearched)
            .Select(x => new QueuedShowDto(x.Show.Id, x.Show.Title, x.Show.PosterUrl, x.Rank, x.Show.Network, x.Show.FirstAirDate?.Year,
                started.Contains(x.Show.Id)))
            .ToList();

        var checks = await db.CatalogChecks.AsNoTracking().ToListAsync(ct);
        var disagreements = checks
            .GroupBy(c => c.ShowId)
            .Select(g => g.OrderBy(c => c.Field).ToList())
            .OrderBy(g => g[0].Rank)
            .Select(g => new DisagreeingShowDto(g[0].ShowId, g[0].ShowTitle, g[0].Rank, saved.Contains(g[0].ShowId), g[0].WikidataUrl,
                g.Select(c => new DisagreementDto(c.Field, c.CatalogValue, c.WikidataValue)).ToList()))
            .ToList();

        return TypedResults.Ok(new ResearchQueueDto(unresearched, disagreements, checks.Count > 0 ? checks.Max(c => c.CheckedAtUtc) : null));
    }
}
