using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ratings.API.Data;
using Ratings.API.Services;

namespace Ratings.API.Apis;

/// <param name="GifId">A GIPHY id, with or instead of <paramref name="Text"/>.</param>
public record PostCommentRequest(Guid ShowId, string? Text, bool IsSpoiler, string? GifId = null);

/// <param name="Text">Null for a spoiler comment the reader hasn't chosen to reveal.</param>
/// <param name="HiddenReason">Set only on the reader's own hidden comment: "flags", "held" or "moderator".</param>
/// <param name="FlaggedByMe">The reader has flagged it.</param>
/// <param name="GifId">The GIPHY id of its GIF; null without one, for a hidden spoiler, and while GIFs are off.</param>
/// <param name="EditedAtUtc">When its author last changed it; null if never.</param>
public record CommentDto(Guid Id, string DisplayName, string? Text, bool IsSpoiler, bool IsMine, DateTime CreatedAtUtc,
    string? HiddenReason = null, bool FlaggedByMe = false, string? GifId = null, DateTime? EditedAtUtc = null);

/// <summary>
/// An author's change to their comment: its words and whether it spoils. Its GIF stays as posted.
/// </summary>
public record EditCommentRequest(string? Text, bool IsSpoiler);

/// <summary>
/// What the reader's browser needs to search GIPHY and show its GIFs.
/// </summary>
public record GifSettingsDto(string ApiKey, string Rating);

/// <param name="HiddenUntilWatched">True when the reader hasn't watched the episode and didn't ask to see comments anyway.</param>
/// <param name="Gifs">Null while GIFs are off (no GIPHY key).</param>
public record CommentsDto(bool HiddenUntilWatched, int TotalCount, IReadOnlyList<CommentDto> Comments, GifSettingsDto? Gifs = null);

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
        api.MapPut("/comments/{id:guid}", EditCommentAsync);
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
        Guid episodeId, ClaimsPrincipal user, RatingsContext db, IOptionsSnapshot<GiphyOptions> giphy, bool reveal = false,
        bool includeSpoilers = false, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var gifs = Gifs.Enabled(giphy.Value) ? new GifSettingsDto(giphy.Value.ApiKey!.Trim(), Gifs.RatingOf(giphy.Value)) : null;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Hidden comments (flagged, held or removed) are seen by their authors only.
        var total = await db.Comments.CountAsync(c => c.EpisodeId == episodeId && (c.HiddenReason == null || c.UserId == userId), ct);
        var watched = await db.WatchedEpisodes.AnyAsync(w => w.UserId == userId && w.EpisodeId == episodeId, ct);

        if (!watched && !reveal)
        {
            return TypedResults.Ok(new CommentsDto(true, total, [], gifs));
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
            .Select(c =>
            {
                var hiddenSpoiler = c.IsSpoiler && !includeSpoilers && c.UserId != userId;
                return new CommentDto(
                    c.Id,
                    c.DisplayName,
                    hiddenSpoiler ? null : c.Text,
                    c.IsSpoiler,
                    c.UserId == userId,
                    c.CreatedAtUtc,
                    c.UserId == userId ? c.HiddenReason : null,
                    flagged.Contains(c.Id),
                    hiddenSpoiler || gifs is null ? null : c.GifId,
                    c.EditedAtUtc);
            })
            .ToList(), gifs));
    }

    private static async Task<Results<Created<CommentDto>, ValidationProblem, ProblemHttpResult>> PostCommentAsync(
        Guid episodeId, PostCommentRequest request, ClaimsPrincipal user, RatingsContext db, Moderation moderation,
        IOptionsSnapshot<GiphyOptions> giphy, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? "";
        var gifId = string.IsNullOrWhiteSpace(request.GifId) ? null : request.GifId.Trim();
        if (gifId is not null && (!Gifs.Enabled(giphy.Value) || !Gifs.IsId(gifId)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["gifId"] = ["That GIF can't be posted."] });
        }
        if (CheckText(text, gifId is not null, moderation, "Write up to 2000 characters, or pick a GIF.") is { } refused)
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
            GifId = gifId,
            IsSpoiler = request.IsSpoiler,
            HiddenReason = moderation.Check(text) == TextVerdict.Held ? HiddenReasons.Held : null,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/v1/comments/{comment.Id}", Mine(comment, giphy.Value));
    }

    /// <summary>
    /// What a comment's words must be, posted or edited: something (words, emoji, or a GIF alone), not too much of
    /// it, and nothing the word check rejects.
    /// </summary>
    private static ValidationProblem? CheckText(string text, bool hasGif, Moderation moderation, string wrongLength) =>
        text.Length > Comment.MaxLength || (text.Length == 0 && !hasGif)
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [wrongLength] })
            : text.Length > 0 ? CommunityApi.CheckPost(text, moderation) : null;

    /// <summary>
    /// A comment as its author gets it back after posting or editing it.
    /// </summary>
    private static CommentDto Mine(Comment comment, GiphyOptions giphy) =>
        new(comment.Id, comment.DisplayName, comment.Text, comment.IsSpoiler, true, comment.CreatedAtUtc, comment.HiddenReason,
            GifId: Gifs.Enabled(giphy) ? comment.GifId : null, EditedAtUtc: comment.EditedAtUtc);

    /// <summary>
    /// Why an edited comment is hidden, from why it was and what the word check makes of its new text. New words are
    /// checked as a new comment's are; an edit never lifts what readers' flags did.
    /// </summary>
    public static string? HiddenAfterEdit(string? was, TextVerdict verdict) =>
        was == HiddenReasons.Flags ? was : verdict == TextVerdict.Held ? HiddenReasons.Held : null;

    private static async Task<Results<Ok<CommentDto>, NotFound, ValidationProblem, Conflict<string>>> EditCommentAsync(
        Guid id, EditCommentRequest request, ClaimsPrincipal user, RatingsContext db, Moderation moderation,
        IOptionsSnapshot<GiphyOptions> giphy, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var comment = await db.Comments.SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (comment is null)
        {
            return TypedResults.NotFound();
        }
        if (comment.HiddenReason == HiddenReasons.Moderator)
        {
            return TypedResults.Conflict("A moderator removed this comment, so it can't be changed.");
        }

        var text = request.Text?.Trim() ?? "";
        if (CheckText(text, comment.GifId is not null, moderation, "Write up to 2000 characters.") is { } refused)
        {
            return refused;
        }

        if (text != comment.Text)
        {
            (comment.Text, comment.EditedAtUtc) = (text, DateTime.UtcNow);
            comment.HiddenReason = HiddenAfterEdit(comment.HiddenReason, moderation.Check(text));
        }
        // The author can always add the spoiler mark, and take off their own; one that readers' flags put there stays.
        comment.IsSpoiler = request.IsSpoiler || (comment.IsSpoiler && await MarkedSpoilerByReadersAsync(db, id, ct));
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(Mine(comment, giphy.Value));
    }

    /// <summary>
    /// Whether enough readers have flagged the comment as a spoiler to mark it one (CommunityApi's flagging does the marking).
    /// </summary>
    private static async Task<bool> MarkedSpoilerByReadersAsync(RatingsContext db, Guid id, CancellationToken ct) =>
        await db.Flags.CountAsync(f => f.TargetKind == CommunityApi.CommentKind && f.TargetId == id && !f.Resolved && f.Reason == "spoiler", ct)
            >= Moderation.SpoilerAfterFlags;

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
