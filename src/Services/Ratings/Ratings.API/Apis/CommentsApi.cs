using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using Ratings.API.Services;

namespace Ratings.API.Apis;

public record PostCommentRequest(Guid ShowId, string Text, bool IsSpoiler);

/// <param name="Text">Null for a spoiler comment the reader hasn't chosen to reveal.</param>
/// <param name="HiddenReason">Set only on the reader's own hidden comment: "flags", "held" or "moderator".</param>
/// <param name="FlaggedByMe">The reader has flagged it.</param>
public record CommentDto(Guid Id, string DisplayName, string? Text, bool IsSpoiler, bool IsMine, DateTime CreatedAtUtc,
    string? HiddenReason = null, bool FlaggedByMe = false);

/// <param name="HiddenUntilWatched">True when the reader hasn't watched the episode and didn't ask to see comments anyway.</param>
public record CommentsDto(bool HiddenUntilWatched, int TotalCount, IReadOnlyList<CommentDto> Comments);

public record ReactRequest(string Emoji);

public record ReactionCountDto(string Emoji, int Count);

public record ReactionsDto(IReadOnlyList<ReactionCountDto> Counts, string? Mine);

public static class CommentsApi
{
    private static readonly HashSet<string> AllowedEmoji = ["😂", "😮", "😢", "😡", "😍", "🔥", "👏", "🤯"];

    public static IEndpointRouteBuilder MapCommentsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/episodes/{episodeId:guid}/comments", GetCommentsAsync);
        api.MapPost("/episodes/{episodeId:guid}/comments", PostCommentAsync);
        api.MapDelete("/comments/{id:guid}", DeleteCommentAsync);

        api.MapGet("/episodes/{episodeId:guid}/reactions", GetReactionsAsync);
        api.MapPut("/episodes/{episodeId:guid}/reaction", ReactAsync);
        api.MapDelete("/episodes/{episodeId:guid}/reaction", RemoveReactionAsync);

        return app;
    }

    /// <summary>
    /// Comments are hidden until the reader has watched the episode, unless <paramref name="reveal"/> is set.
    /// Spoiler-flagged comments additionally need <paramref name="includeSpoilers"/>.
    /// </summary>
    private static async Task<Ok<CommentsDto>> GetCommentsAsync(
        Guid episodeId, ClaimsPrincipal user, RatingsContext db, bool reveal = false, bool includeSpoilers = false,
        int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Hidden comments (flagged, held or removed) are seen by their authors only.
        var total = await db.Comments.CountAsync(c => c.EpisodeId == episodeId && (c.HiddenReason == null || c.UserId == userId), ct);
        var watched = await db.WatchedEpisodes.AnyAsync(w => w.UserId == userId && w.EpisodeId == episodeId, ct);

        if (!watched && !reveal)
        {
            return TypedResults.Ok(new CommentsDto(true, total, []));
        }

        var comments = await db.Comments.AsNoTracking()
            .Where(c => c.EpisodeId == episodeId && (c.HiddenReason == null || c.UserId == userId))
            .OrderByDescending(c => c.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        var ids = comments.Select(c => c.Id).ToList();
        var flagged = (await db.Flags.Where(f => f.TargetKind == CommunityApi.CommentKind && f.UserId == userId && ids.Contains(f.TargetId))
            .Select(f => f.TargetId).ToListAsync(ct)).ToHashSet();

        return TypedResults.Ok(new CommentsDto(false, total, comments
            .Select(c => new CommentDto(
                c.Id,
                c.DisplayName,
                c.IsSpoiler && !includeSpoilers && c.UserId != userId ? null : c.Text,
                c.IsSpoiler,
                c.UserId == userId,
                c.CreatedAtUtc,
                c.UserId == userId ? c.HiddenReason : null,
                flagged.Contains(c.Id)))
            .ToList()));
    }

    private static async Task<Results<Created<CommentDto>, ValidationProblem, ProblemHttpResult>> PostCommentAsync(
        Guid episodeId, PostCommentRequest request, ClaimsPrincipal user, RatingsContext db, Moderation moderation, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? "";
        if (text.Length is 0 or > 2000)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["Comments must be 1 to 2000 characters."] });
        }
        if (CommunityApi.CheckPost(text, moderation) is { } refused)
        {
            return refused;
        }
        if (await Moderation.TooManyPostsAsync(db, user.GetUserId(), DateTime.UtcNow, ct))
        {
            return CommunityApi.TooMany("posts");
        }

        var comment = new Comment
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episodeId,
            ShowId = request.ShowId,
            UserId = user.GetUserId(),
            DisplayName = user.GetDisplayName() ?? "Viewer",
            Text = text,
            IsSpoiler = request.IsSpoiler,
            HiddenReason = moderation.Check(text) == TextVerdict.Held ? HiddenReasons.Held : null,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/v1/comments/{comment.Id}",
            new CommentDto(comment.Id, comment.DisplayName, comment.Text, comment.IsSpoiler, true, comment.CreatedAtUtc, comment.HiddenReason));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteCommentAsync(Guid id, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var deleted = await db.Comments.Where(c => c.Id == id && c.UserId == user.GetUserId()).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return TypedResults.NotFound();
        }
        await db.Flags.Where(f => f.TargetKind == CommunityApi.CommentKind && f.TargetId == id).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ReactionsDto>> GetReactionsAsync(Guid episodeId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var counts = await db.Reactions
            .Where(r => r.EpisodeId == episodeId)
            .GroupBy(r => r.Emoji)
            .Select(g => new ReactionCountDto(g.Key, g.Count()))
            .ToListAsync(ct);
        var mine = await db.Reactions
            .Where(r => r.EpisodeId == episodeId && r.UserId == userId)
            .Select(r => r.Emoji)
            .SingleOrDefaultAsync(ct);

        return TypedResults.Ok(new ReactionsDto(counts.OrderByDescending(c => c.Count).ToList(), mine));
    }

    private static async Task<Results<NoContent, ValidationProblem>> ReactAsync(
        Guid episodeId, ReactRequest request, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        if (!AllowedEmoji.Contains(request.Emoji))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["emoji"] = [$"Use one of: {string.Join(" ", AllowedEmoji)}"]
            });
        }

        var userId = user.GetUserId();
        var reaction = await db.Reactions.FindAsync([userId, episodeId], ct);
        if (reaction is null)
        {
            db.Reactions.Add(new Reaction { UserId = userId, EpisodeId = episodeId, Emoji = request.Emoji, CreatedAtUtc = DateTime.UtcNow });
        }
        else
        {
            reaction.Emoji = request.Emoji;
            reaction.CreatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RemoveReactionAsync(Guid episodeId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await db.Reactions.Where(r => r.UserId == userId && r.EpisodeId == episodeId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }
}
