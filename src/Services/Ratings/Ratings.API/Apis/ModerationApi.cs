using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using Ratings.API.Services;
using System.Security.Claims;

namespace Ratings.API.Apis;

public record PostFlagDto(string Reason, string? Note, DateTime CreatedAtUtc);

/// <param name="Kind">"comment" or "insight".</param>
/// <param name="HiddenReason">"flags" or "held" while it waits; null when it's still showing.</param>
public record ModeratedPostDto(string Kind, Guid Id, Guid ShowId, Guid? EpisodeId, string Author, string Text, bool IsSpoiler,
    string? HiddenReason, DateTime CreatedAtUtc, IReadOnlyList<PostFlagDto> Flags);

public record ModerationActionDto(string Moderator, string Action, string TargetKind, Guid TargetId, string? Summary, DateTime AtUtc);

/// <param name="Accepted">Recently accepted corrections, which can be reverted.</param>
public record ModerationQueueDto(IReadOnlyList<ModeratedPostDto> Posts, IReadOnlyList<CorrectionDto> Corrections,
    IReadOnlyList<CorrectionDto> Accepted, IReadOnlyList<ModerationActionDto> Log);

/// <param name="Value">The value to apply, when the moderator edits the suggestion; null takes it as suggested.</param>
public record AcceptCorrectionRequest(string? Value);

/// <summary>
/// The moderation page's API, for moderators only (<see cref="Moderation.IsModerator"/>); 404 for anyone else.
/// Posts wait here while they have flags nobody has dealt with, or are hidden by flags or held when posted. Restoring
/// shows a post again and settles its flags; removing hides it for good. Corrections are accepted (applied to the catalog
/// by the sync worker, except "other", which is only recorded), rejected, or reverted later. Every action is logged.
/// </summary>
public static class ModerationApi
{
    private const int LogShown = 30;
    private const int AcceptedShown = 20;

    public static void MapModerationApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/moderation").RequireAuthorization();
        api.MapGet("", GetQueueAsync);
        api.MapPost("/posts/{kind}/{id:guid}/restore", (string kind, Guid id, ClaimsPrincipal user, RatingsContext db, Moderation moderation, CancellationToken ct) =>
            SettleAsync(kind, id, restore: true, user, db, moderation, ct));
        api.MapPost("/posts/{kind}/{id:guid}/remove", (string kind, Guid id, ClaimsPrincipal user, RatingsContext db, Moderation moderation, CancellationToken ct) =>
            SettleAsync(kind, id, restore: false, user, db, moderation, ct));
        api.MapPost("/corrections/{id:guid}/accept", AcceptAsync);
        api.MapPost("/corrections/{id:guid}/reject", RejectAsync);
        api.MapPost("/corrections/{id:guid}/revert", RevertAsync);
    }

    private static async Task<Results<Ok<ModerationQueueDto>, NotFound>> GetQueueAsync(ClaimsPrincipal user, RatingsContext db, Moderation moderation, CancellationToken ct)
    {
        if (!moderation.IsModerator(user))
        {
            return TypedResults.NotFound();
        }

        var flags = (await db.Flags.AsNoTracking().Where(f => !f.Resolved).ToListAsync(ct)).ToLookup(f => (f.TargetKind, f.TargetId));
        var flaggedComments = flags.Where(g => g.Key.TargetKind == CommunityApi.CommentKind).Select(g => g.Key.TargetId).ToList();
        var flaggedInsights = flags.Where(g => g.Key.TargetKind == CommunityApi.InsightKind).Select(g => g.Key.TargetId).ToList();
        string[] waiting = [HiddenReasons.Flags, HiddenReasons.Held];

        var comments = await db.Comments.AsNoTracking()
            .Where(c => flaggedComments.Contains(c.Id) || waiting.Contains(c.HiddenReason!)).ToListAsync(ct);
        var insights = await db.Insights.AsNoTracking()
            .Where(i => flaggedInsights.Contains(i.Id) || waiting.Contains(i.HiddenReason!)).ToListAsync(ct);

        List<PostFlagDto> FlagsOf(string kind, Guid id) =>
            flags[(kind, id)].OrderBy(f => f.CreatedAtUtc).Select(f => new PostFlagDto(f.Reason, f.Note, f.CreatedAtUtc)).ToList();

        var posts = comments
            .Select(c => new ModeratedPostDto(CommunityApi.CommentKind, c.Id, c.ShowId, c.EpisodeId, c.DisplayName, c.Text, c.IsSpoiler,
                c.HiddenReason, c.CreatedAtUtc, FlagsOf(CommunityApi.CommentKind, c.Id)))
            .Concat(insights.Select(i => new ModeratedPostDto(CommunityApi.InsightKind, i.Id, i.ShowId, i.EpisodeId, i.DisplayName, i.Text,
                i.IsSpoiler, i.HiddenReason, i.CreatedAtUtc, FlagsOf(CommunityApi.InsightKind, i.Id))))
            .Where(p => p.HiddenReason != HiddenReasons.Moderator)
            // Hidden ones first (people are waiting on them), then the most flagged.
            .OrderBy(p => p.HiddenReason is null ? 1 : 0).ThenByDescending(p => p.Flags.Count).ThenBy(p => p.CreatedAtUtc)
            .ToList();

        var pending = await db.Corrections.AsNoTracking().Where(c => c.Status == "pending").OrderBy(c => c.CreatedAtUtc).ToListAsync(ct);
        var accepted = await db.Corrections.AsNoTracking().Where(c => c.Status == "accepted")
            .OrderByDescending(c => c.ReviewedAtUtc).Take(AcceptedShown).ToListAsync(ct);
        var log = await db.ModerationActions.AsNoTracking().OrderByDescending(a => a.AtUtc).Take(LogShown)
            .Select(a => new ModerationActionDto(a.ModeratorName, a.Action, a.TargetKind, a.TargetId, a.Summary, a.AtUtc)).ToListAsync(ct);

        return TypedResults.Ok(new ModerationQueueDto(posts, pending.Select(CommunityApi.ToDto).ToList(),
            accepted.Select(CommunityApi.ToDto).ToList(), log));
    }

    private static async Task<Results<NoContent, NotFound>> SettleAsync(
        string kind, Guid id, bool restore, ClaimsPrincipal user, RatingsContext db, Moderation moderation, CancellationToken ct)
    {
        if (!moderation.IsModerator(user) || kind is not (CommunityApi.CommentKind or CommunityApi.InsightKind))
        {
            return TypedResults.NotFound();
        }
        IModeratedPost? post = kind == CommunityApi.CommentKind ? await db.Comments.FindAsync([id], ct) : await db.Insights.FindAsync([id], ct);
        if (post is null)
        {
            return TypedResults.NotFound();
        }

        post.HiddenReason = restore ? null : HiddenReasons.Moderator;
        await db.Flags.Where(f => f.TargetKind == kind && f.TargetId == id && !f.Resolved)
            .ExecuteUpdateAsync(f => f.SetProperty(x => x.Resolved, true), ct);
        var text = post is Comment c ? c.Text : ((CommunityInsight)post).Text;
        Log(db, user, restore ? "restore" : "remove", kind, id, text);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CorrectionDto>, NotFound, ValidationProblem, ProblemHttpResult>> AcceptAsync(
        Guid id, AcceptCorrectionRequest? request, ClaimsPrincipal user, RatingsContext db, Moderation moderation, CatalogSyncClient sync,
        CancellationToken ct)
    {
        if (!moderation.IsModerator(user) || await db.Corrections.FindAsync([id], ct) is not { Status: "pending" } correction)
        {
            return TypedResults.NotFound();
        }

        var value = string.IsNullOrWhiteSpace(request?.Value) ? correction.SuggestedValue : request.Value.Trim();
        if (CommunityApi.ValidateCorrection(correction.Field, correction.EpisodeId, value) is { } error)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["value"] = [error] });
        }

        // "other" can't be applied automatically; accepting records that it's right, for the moderator to act on.
        if (correction.Field != "other")
        {
            var (outcome, detail) = await sync.ApplyAsync(correction.Id, correction.ShowId, correction.EpisodeId, correction.Field, value, ct);
            switch (outcome)
            {
                case OverrideOutcome.NotInCatalog:
                    return TypedResults.Problem("That show or episode isn't in the catalog any more.", statusCode: StatusCodes.Status409Conflict);
                case OverrideOutcome.Invalid:
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["value"] = [detail ?? "The catalog refused the value."] });
                case OverrideOutcome.Unavailable:
                    return TypedResults.Problem("The catalog can't be reached right now; try again in a moment.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        correction.SuggestedValue = value;
        Review(correction, user, "accepted");
        Log(db, user, "accept", "correction", id, $"{correction.Context}: {correction.Field} → {value}");
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(CommunityApi.ToDto(correction));
    }

    private static async Task<Results<Ok<CorrectionDto>, NotFound>> RejectAsync(Guid id, ClaimsPrincipal user, RatingsContext db, Moderation moderation, CancellationToken ct)
    {
        if (!moderation.IsModerator(user) || await db.Corrections.FindAsync([id], ct) is not { Status: "pending" } correction)
        {
            return TypedResults.NotFound();
        }
        Review(correction, user, "rejected");
        Log(db, user, "reject", "correction", id, $"{correction.Context}: {correction.Field}");
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(CommunityApi.ToDto(correction));
    }

    /// <summary>Undoes an accepted correction: the provider's value comes back on the show's next refresh.</summary>
    private static async Task<Results<Ok<CorrectionDto>, NotFound, ProblemHttpResult>> RevertAsync(
        Guid id, ClaimsPrincipal user, RatingsContext db, Moderation moderation, CatalogSyncClient sync, CancellationToken ct)
    {
        if (!moderation.IsModerator(user) || await db.Corrections.FindAsync([id], ct) is not { Status: "accepted" } correction)
        {
            return TypedResults.NotFound();
        }
        if (correction.Field != "other" && !await sync.RemoveAsync(id, ct))
        {
            return TypedResults.Problem("The catalog can't be reached right now; try again in a moment.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        Review(correction, user, "reverted");
        Log(db, user, "revert", "correction", id, $"{correction.Context}: {correction.Field}");
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(CommunityApi.ToDto(correction));
    }

    private static void Review(Correction correction, ClaimsPrincipal user, string status)
    {
        correction.Status = status;
        correction.ReviewedBy = user.GetDisplayName() ?? user.FindFirstValue("preferred_username");
        correction.ReviewedAtUtc = DateTime.UtcNow;
    }

    private static void Log(RatingsContext db, ClaimsPrincipal user, string action, string kind, Guid id, string? summary) =>
        db.ModerationActions.Add(new ModerationAction
        {
            Id = Guid.CreateVersion7(),
            ModeratorId = user.GetUserId(),
            ModeratorName = user.GetDisplayName() ?? user.FindFirstValue("preferred_username") ?? "Moderator",
            Action = action,
            TargetKind = kind,
            TargetId = id,
            Summary = summary is null ? null : summary[..Math.Min(300, summary.Length)],
            AtUtc = DateTime.UtcNow
        });
}
