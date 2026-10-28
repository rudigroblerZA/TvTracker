using Microsoft.EntityFrameworkCore;
using Research.API.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Research.API.Apis;

/// <param name="Dossiers">Shows a moderator has started researching.</param>
/// <param name="ShowsSaved">Shows with a saved version: a trusted record.</param>
/// <param name="Drafting">Drafts the model is writing now.</param>
/// <param name="UnsentEvents">Episode-note events still in the outbox (not yet published, or failed).</param>
public record ResearchOpsStats(int Dossiers, int ShowsSaved, int Versions, DateTime? LastSavedAtUtc, int Drafting, int Sources,
    int EpisodeNotes, int Essential, int Skippable, int NotedShows, int UnsentEvents);

/// <summary>
/// Counts for the developer ops dashboard that Catalog.API serves. Mapped in Development only and without sign-in,
/// like Analytics', since Catalog.API calls it server-side.
/// </summary>
public static class OpsApi
{
    public static void MapOpsApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/stats", GetStatsAsync);

    private static async Task<ResearchOpsStats> GetStatsAsync(ResearchContext db, CancellationToken ct) =>
        new(await db.Dossiers.CountAsync(ct),
            await db.Saved.Select(s => s.ShowId).Distinct().CountAsync(ct),
            await db.Saved.CountAsync(ct),
            await db.Saved.MaxAsync(s => (DateTime?)s.SavedAtUtc, ct),
            await db.Dossiers.CountAsync(d => d.DraftingSinceUtc != null, ct),
            await db.Sources.CountAsync(ct),
            await db.EpisodeNotes.CountAsync(ct),
            await db.EpisodeNotes.CountAsync(n => n.Mark == EpisodeMarks.Essential, ct),
            await db.EpisodeNotes.CountAsync(n => n.Mark == EpisodeMarks.Skippable, ct),
            await db.EpisodeNotes.Select(n => n.ShowId).Distinct().CountAsync(ct),
            await db.IntegrationEventLogs.CountAsync(e => e.State != EventState.Published, ct));
}
