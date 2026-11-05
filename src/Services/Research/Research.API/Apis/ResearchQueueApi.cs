using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Research.API.Data;
using Research.API.Services;
using TvTracker;

namespace Research.API.Apis;

/// <param name="Rank">The show's place among the catalog's most popular, 1 first.</param>
/// <param name="Started">A moderator has gathered or drafted for it, but not saved.</param>
/// <param name="Prepared">The research assistant gathered and drafted for it, for a moderator to review.</param>
public record QueuedShowDto(Guid ShowId, string Title, string? PosterUrl, int Rank, string? Network, int? Year, bool Started, bool Prepared = false);

/// <param name="Field">"network", "premiered" or "episodes".</param>
public record DisagreementDto(string Field, string? Catalog, string Wikidata);

/// <param name="Researched">It has saved research; saving a picked value overrides the catalog's.</param>
public record DisagreeingShowDto(Guid ShowId, string Title, int Rank, bool Researched, string WikidataUrl, IReadOnlyList<DisagreementDto> Fields);

/// <param name="CheckedAtUtc">When the shows were last checked against Wikidata; null before the first check.</param>
/// <param name="Refresh">Saved research due for a refresh (<see cref="RefreshQueue"/>).</param>
public record ResearchQueueDto(IReadOnlyList<QueuedShowDto> Unresearched, IReadOnlyList<DisagreeingShowDto> Disagreements, DateTime? CheckedAtUtc,
    IReadOnlyList<RefreshShowDto>? Refresh = null);

/// <summary>
/// What to research next, for moderators: the catalog's most popular shows with no saved research, and popular shows
/// where Wikidata disagrees with the catalog (see <see cref="CatalogChecks"/>). Anyone else gets 404, like the rest of
/// research.
/// </summary>
public static class ResearchQueueApi
{
    private const int MaxUnresearched = 30;

    public static void MapResearchQueueApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/research/queue", GetQueueAsync).RequireAuthorization().RequireFeature(FeatureFlags.Research);
        app.MapPost("/api/v1/research/assistant/run", RunAssistant).RequireAuthorization()
            .RequireFeature(FeatureFlags.Research).RequireFeature(FeatureFlags.ResearchAssistant);
    }

    /// <summary>
    /// Starts the research assistant's run now; 202, and the queue shows what it prepared once the drafts are done.
    /// </summary>
    private static Results<Accepted, NotFound> RunAssistant(ClaimsPrincipal user, Researchers researchers, ResearchAssistant assistant)
    {
        if (!researchers.Allowed(user))
        {
            return TypedResults.NotFound();
        }
        assistant.RunNow();
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Ok<ResearchQueueDto>, NotFound>> GetQueueAsync(
        ClaimsPrincipal user, Researchers researchers, ResearchContext db, CatalogReader catalog, IMemoryCache cache, TimeProvider time,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (!researchers.Allowed(user))
        {
            return TypedResults.NotFound();
        }

        var saved = (await db.Saved.Select(s => s.ShowId).Distinct().ToListAsync(ct)).ToHashSet();
        // Started: someone has worked on it. Prepared: the assistant's preparation is the last change to it.
        var dossiers = await db.Dossiers.AsNoTracking()
            .Select(d => new Dossier { ShowId = d.ShowId, ShowTitle = d.ShowTitle, UpdatedAtUtc = d.UpdatedAtUtc, AssistantPreparedAtUtc = d.AssistantPreparedAtUtc })
            .ToDictionaryAsync(d => d.ShowId, ct);
        var popular = await catalog.GetPopularFactsAsync(CatalogChecks.Popular, ct);
        var unresearched = popular
            .Select((s, i) => (Show: s, Rank: i + 1))
            .Where(x => !saved.Contains(x.Show.Id))
            .Take(MaxUnresearched)
            .Select(x => new QueuedShowDto(x.Show.Id, x.Show.Title, x.Show.PosterUrl, x.Rank, x.Show.Network, x.Show.FirstAirDate?.Year,
                dossiers.TryGetValue(x.Show.Id, out var d) && d.UpdatedAtUtc > DateTime.UnixEpoch && !ResearchAssistant.PreparedLast(d),
                d is not null && ResearchAssistant.PreparedLast(d)))
            .ToList();

        var checks = await db.CatalogChecks.AsNoTracking().ToListAsync(ct);
        var disagreements = checks
            .GroupBy(c => c.ShowId)
            .Select(g => g.OrderBy(c => c.Field).ToList())
            .OrderBy(g => g[0].Rank)
            .Select(g => new DisagreeingShowDto(g[0].ShowId, g[0].ShowTitle, g[0].Rank, saved.Contains(g[0].ShowId), g[0].WikidataUrl,
                g.Select(c => new DisagreementDto(c.Field, c.CatalogValue, c.WikidataValue)).ToList()))
            .ToList();

        var refresh = await RefreshQueue.ComputeAsync(db, catalog, cache, popular, time.GetUtcNow().UtcDateTime,
            loggers.CreateLogger(nameof(ResearchQueueApi)), ct);
        return TypedResults.Ok(new ResearchQueueDto(unresearched, disagreements, checks.Count > 0 ? checks.Max(c => c.CheckedAtUtc) : null, refresh));
    }
}
