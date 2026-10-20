using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Recommendations.API.Data;
using Recommendations.API.Services;

namespace Recommendations.API.Apis;

public record RecommendedShowDto(Guid ShowId, string Title, string? PosterUrl, double Score, string Reason);

public static class RecommendationsApi
{
    public static IEndpointRouteBuilder MapRecommendationsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/shows/{showId:guid}/similar", GetSimilarAsync);

        var mine = api.MapGroup("").RequireAuthorization();
        mine.MapGet("/for-you", GetForYouAsync);
        mine.MapPost("/dismissals/{showId:guid}", DismissAsync);
        mine.MapDelete("/dismissals/{showId:guid}", UndismissAsync);
        mine.MapGet("/me/export", ExportAsync);

        return app;
    }

    private static async Task<Ok<List<RecommendedShowDto>>> GetSimilarAsync(
        Guid showId, RecommendationEngine engine, RecommendationsContext db, HybridCache cache, int limit = 10, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 30);

        var similar = await cache.GetOrCreateAsync(
            $"recs:similar:{showId}:{limit}",
            async token => await ToDtosAsync(await engine.GetSimilarAsync(showId, limit, token), db, token),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30), LocalCacheExpiration = TimeSpan.FromMinutes(5) },
            cancellationToken: ct);

        return TypedResults.Ok(similar);
    }

    private static async Task<Ok<List<RecommendedShowDto>>> GetForYouAsync(
        ClaimsPrincipal user, RecommendationStore store, RecommendationsContext db, int limit = 20, CancellationToken ct = default)
    {
        var rows = await store.GetOrBuildAsync(user.GetUserId(), ct);
        var picks = rows
            .Take(Math.Clamp(limit, 1, RecommendationRefresher.ListSize))
            .Select(r => new ScoredShow(r.ShowId, r.Score, r.Reason))
            .ToList();

        return TypedResults.Ok(await ToDtosAsync(picks, db, ct));
    }

    private static async Task<NoContent> DismissAsync(
        Guid showId, ClaimsPrincipal user, RecommendationsContext db, RecommendationStore store, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await db.Dismissals.FindAsync([userId, showId], ct) is null)
        {
            db.Dismissals.Add(new Dismissal { UserId = userId, ShowId = showId, DismissedAtUtc = DateTime.UtcNow });
        }

        // Remove it from the current list straight away; the full rebuild happens in the background.
        await db.UserRecommendations.Where(r => r.UserId == userId && r.ShowId == showId).ExecuteDeleteAsync(ct);
        await store.MarkDirtyAsync(userId, ct);
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UndismissAsync(
        Guid showId, ClaimsPrincipal user, RecommendationsContext db, RecommendationStore store, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await db.Dismissals.Where(d => d.UserId == userId && d.ShowId == showId).ExecuteDeleteAsync(ct);
        await store.MarkDirtyAsync(userId, ct);
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<IResult> ExportAsync(ClaimsPrincipal user, RecommendationsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var dismissed = await db.Dismissals.AsNoTracking().Where(d => d.UserId == userId)
            .Select(d => new { d.ShowId, d.DismissedAtUtc })
            .ToListAsync(ct);

        return Results.Ok(new { dismissedShows = dismissed });
    }

    private static async Task<List<RecommendedShowDto>> ToDtosAsync(List<ScoredShow> picks, RecommendationsContext db, CancellationToken ct)
    {
        var ids = picks.Select(p => p.ShowId).ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        return picks
            .Where(p => shows.ContainsKey(p.ShowId))
            .Select(p => new RecommendedShowDto(p.ShowId, shows[p.ShowId].Title, shows[p.ShowId].PosterUrl, p.Score, p.Reason))
            .ToList();
    }
}
