using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Research.API.Data;

namespace Research.API.Apis;

public record ExportedResearchVersion(Guid ShowId, int Version, DateTime SavedAtUtc, string SavedAs, string Insight);

public record ExportedEpisodeNote(Guid ShowId, Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Mark, string Reason,
    string SourceUrl, DateTime NotedAtUtc, string NotedAs);

public record ResearchExport(IReadOnlyList<ExportedResearchVersion> SavedVersions, IReadOnlyList<ExportedEpisodeNote> EpisodeNotes);

/// <summary>
/// The caller's part of the data export: the research versions they saved and the episode notes they wrote, which
/// carry their id and name (both are cleared from them when the account is deleted). Anyone signed in may ask; most
/// people have written none. Not behind the research flag: it's the user's data either way.
/// </summary>
public static class ExportApi
{
    public static void MapExportApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/me/export", ExportAsync).RequireAuthorization();

    private static async Task<Results<Ok<ResearchExport>, UnauthorizedHttpResult>> ExportAsync(ClaimsPrincipal user, ResearchContext db, CancellationToken ct)
    {
        // A token with no user (client credentials) has no export, and a null id would match what deleted moderators
        // left behind (their ids are cleared).
        if (user.FindFirstValue("sub") is not { } id)
        {
            return TypedResults.Unauthorized();
        }
        var versions = await db.Saved.AsNoTracking()
            .Where(s => s.SavedById == id)
            .OrderBy(s => s.SavedAtUtc)
            .Select(s => new ExportedResearchVersion(s.ShowId, s.Version, s.SavedAtUtc, s.SavedByName, s.Insight))
            .ToListAsync(ct);
        var notes = await db.EpisodeNotes.AsNoTracking()
            .Where(n => n.NotedById == id)
            .OrderBy(n => n.NotedAtUtc)
            .Select(n => new ExportedEpisodeNote(n.ShowId, n.EpisodeId, n.SeasonNumber, n.EpisodeNumber, n.Mark, n.Reason,
                n.SourceUrl, n.NotedAtUtc, n.NotedByName))
            .ToListAsync(ct);
        return TypedResults.Ok(new ResearchExport(versions, notes));
    }
}
