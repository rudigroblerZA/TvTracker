using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ratings.Data;
using TvTracker;

namespace Ratings.API.Apis;

public static class ExportApi
{
    public static void MapExportApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/me/export", ExportAsync).RequireAuthorization();

    /// <summary>
    /// What the caller rated, wrote, reacted to, voted on and reported, collected by Identity.API's account export.
    /// </summary>
    internal static async Task<IResult> ExportAsync(ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        return Results.Ok(new
        {
            showRatings = await db.ShowRatings.AsNoTracking().Where(r => r.UserId == userId)
                .Select(r => new { r.ShowId, r.Stars, r.RatedAtUtc }).ToListAsync(ct),
            episodeRatings = await db.EpisodeRatings.AsNoTracking().Where(r => r.UserId == userId)
                .Select(r => new { r.ShowId, r.EpisodeId, r.Stars, r.RatedAtUtc }).ToListAsync(ct),
            comments = await db.Comments.AsNoTracking().Where(c => c.UserId == userId)
                .Select(c => new { c.ShowId, c.EpisodeId, c.Text, c.GifId, c.IsSpoiler, c.CreatedAtUtc, c.EditedAtUtc }).ToListAsync(ct),
            reactions = await db.Reactions.AsNoTracking().Where(r => r.UserId == userId)
                .Select(r => new { r.EpisodeId, r.Emoji, r.CreatedAtUtc }).ToListAsync(ct),
            communityInsights = await db.Insights.AsNoTracking().Where(i => i.UserId == userId)
                .Select(i => new { i.ShowId, i.EpisodeId, i.Text, i.IsSpoiler, i.HiddenReason, i.CreatedAtUtc }).ToListAsync(ct),
            helpfulVotes = await db.InsightVotes.AsNoTracking().Where(v => v.UserId == userId)
                .Select(v => new { v.InsightId, v.CreatedAtUtc }).ToListAsync(ct),
            flags = await db.Flags.AsNoTracking().Where(f => f.UserId == userId)
                .Select(f => new { f.TargetKind, f.TargetId, f.Reason, f.Note, f.CreatedAtUtc }).ToListAsync(ct),
            corrections = await db.Corrections.AsNoTracking().Where(c => c.UserId == userId)
                .Select(c => new { c.ShowId, c.EpisodeId, c.Field, c.SuggestedValue, c.Note, c.Status, c.CreatedAtUtc }).ToListAsync(ct),
            episodeDuels = await db.Duels.AsNoTracking().Where(d => d.UserId == userId)
                .Select(d => new { d.ShowId, d.WinnerId, d.LoserId, d.DecidedAtUtc }).ToListAsync(ct),
            characterVotes = await db.CharacterVotes.AsNoTracking().Where(v => v.UserId == userId)
                .Select(v => new { v.ShowId, v.EpisodeId, v.PersonId, v.CreatedAtUtc }).ToListAsync(ct),
            // What they did as a moderator, if they are one.
            moderationActions = await db.ModerationActions.AsNoTracking().Where(a => a.ModeratorId == userId)
                .Select(a => new { a.Action, a.TargetKind, a.TargetId, a.Summary, a.AtUtc }).ToListAsync(ct)
        });
    }
}
