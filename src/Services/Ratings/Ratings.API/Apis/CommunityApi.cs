using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using Ratings.API.Services;
using TvTracker;

namespace Ratings.API.Apis;

public record FlagRequest(string Reason, string? Note);

/// <param name="Hidden">Enough people have flagged it that it's hidden until a moderator looks.</param>
/// <param name="MarkedSpoiler">Flags made it a spoiler.</param>
public record FlagResultDto(bool Hidden, bool MarkedSpoiler);

public record PostInsightRequest(Guid? EpisodeId, string Text, bool IsSpoiler);

/// <param name="HiddenReason">Set only on the reader's own hidden insight.</param>
public record CommunityInsightDto(Guid Id, Guid ShowId, Guid? EpisodeId, string DisplayName, string Text, bool IsSpoiler, int Helpful,
    bool VotedHelpful, bool IsMine, bool FlaggedByMe, string? HiddenReason, DateTime CreatedAtUtc);

public record CommunityInsightsDto(int Total, IReadOnlyList<CommunityInsightDto> Insights);

public record SuggestCorrectionRequest(Guid ShowId, Guid? EpisodeId, string Field, string? CurrentValue, string SuggestedValue,
    string? Note, string? Context);

public record CorrectionDto(Guid Id, Guid ShowId, Guid? EpisodeId, string Field, string? CurrentValue, string SuggestedValue,
    string? Note, string? Context, string DisplayName, string Status, string? ReviewedBy, DateTime? ReviewedAtUtc, DateTime CreatedAtUtc);

/// <summary>
/// What viewers add beside comments: flags on comments and community insights, community insights themselves (tips
/// about a show or an episode, ranked by how many found them helpful), and suggested corrections to the catalog.
/// Moderators handle what comes of them on the moderation page (<see cref="ModerationApi"/>).
/// </summary>
public static class CommunityApi
{
    public const string CommentKind = "comment";
    public const string InsightKind = "insight";

    public static readonly IReadOnlySet<string> CorrectionFields = new HashSet<string>
    {
        "show-summary", "episode-title", "episode-summary", "episode-airdate", "episode-runtime", "other"
    };

    private const int InsightMinLength = 10;
    private const int InsightMaxLength = 1000;
    private const int InsightsShown = 20;

    public static void MapCommunityApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        // Flagging is part of keeping things civil, so it isn't behind a feature flag.
        api.MapPost("/comments/{id:guid}/flag", (Guid id, FlagRequest request, ClaimsPrincipal user, RatingsContext db, CancellationToken ct) =>
            FlagAsync(CommentKind, id, request, user, db, ct));
        api.MapPost("/community-insights/{id:guid}/flag", (Guid id, FlagRequest request, ClaimsPrincipal user, RatingsContext db, CancellationToken ct) =>
            FlagAsync(InsightKind, id, request, user, db, ct));

        var insights = api.MapGroup("").RequireFeature(FeatureFlags.CommunityInsights);
        insights.MapGet("/shows/{showId:guid}/community-insights", GetInsightsAsync);
        insights.MapPost("/shows/{showId:guid}/community-insights", PostInsightAsync);
        insights.MapDelete("/community-insights/{id:guid}", DeleteInsightAsync);
        insights.MapPut("/community-insights/{id:guid}/helpful", VoteAsync);
        insights.MapDelete("/community-insights/{id:guid}/helpful", UnvoteAsync);

        var corrections = api.MapGroup("").RequireFeature(FeatureFlags.Corrections);
        corrections.MapPost("/corrections", SuggestAsync);
        corrections.MapGet("/me/corrections", GetMyCorrectionsAsync);
    }

    /// <summary>Refuses text with a blocked term; null when it may be posted (perhaps held for review).</summary>
    public static ValidationProblem? CheckPost(string text, Moderation moderation) =>
        moderation.Check(text) == TextVerdict.Rejected
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["That contains words that aren't allowed here."] })
            : null;

    public static ProblemHttpResult TooMany(string what) =>
        TypedResults.Problem($"That's a lot of {what} in an hour. Please wait a little.", statusCode: StatusCodes.Status429TooManyRequests);

    private static async Task<Results<Ok<FlagResultDto>, NotFound, ValidationProblem, ProblemHttpResult>> FlagAsync(
        string kind, Guid id, FlagRequest request, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        if (!Moderation.FlagReasons.Contains(request.Reason ?? ""))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = [$"Use one of: {string.Join(", ", Moderation.FlagReasons)}."] });
        }

        IModeratedPost? post = kind == CommentKind ? await db.Comments.FindAsync([id], ct) : await db.Insights.FindAsync([id], ct);
        if (post is null || post.HiddenReason == HiddenReasons.Moderator)
        {
            return TypedResults.NotFound();
        }

        var userId = user.GetUserId();
        if (post.UserId == userId)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["You can't flag your own post."] });
        }
        var now = DateTime.UtcNow;
        if (!await db.Flags.AnyAsync(f => f.TargetKind == kind && f.TargetId == id && f.UserId == userId, ct))
        {
            if (await Moderation.TooManyFlagsAsync(db, userId, now, ct))
            {
                return TooMany("flags");
            }
            db.Flags.Add(new ContentFlag
            {
                Id = Guid.CreateVersion7(),
                TargetKind = kind,
                TargetId = id,
                UserId = userId,
                Reason = request.Reason!,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(500, request.Note.Trim().Length)],
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
        }

        // Only flags a moderator hasn't dealt with count, so a restored post needs fresh ones to be hidden again.
        var open = await db.Flags.Where(f => f.TargetKind == kind && f.TargetId == id && !f.Resolved).Select(f => f.Reason).ToListAsync(ct);
        var markedSpoiler = false;
        if (!post.IsSpoiler && open.Count(r => r == "spoiler") >= Moderation.SpoilerAfterFlags)
        {
            post.IsSpoiler = markedSpoiler = true;
        }
        if (post.HiddenReason is null && open.Count >= Moderation.HideAfterFlags)
        {
            post.HiddenReason = HiddenReasons.Flags;
        }
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new FlagResultDto(post.HiddenReason is not null, markedSpoiler));
    }

    /// <summary>
    /// A show's insights (no <paramref name="episodeId"/>) or one episode's: visible ones and the reader's own hidden
    /// ones, most helpful first, then newest.
    /// </summary>
    private static async Task<Ok<CommunityInsightsDto>> GetInsightsAsync(Guid showId, Guid? episodeId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var query = db.Insights.AsNoTracking()
            .Where(i => i.ShowId == showId && i.EpisodeId == episodeId && (i.HiddenReason == null || i.UserId == userId));
        var total = await query.CountAsync(ct);
        var rows = await query
            .Select(i => new { Insight = i, Helpful = db.InsightVotes.Count(v => v.InsightId == i.Id) })
            .OrderByDescending(x => x.Helpful).ThenByDescending(x => x.Insight.CreatedAtUtc)
            .Take(InsightsShown)
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Insight.Id).ToList();
        var voted = (await db.InsightVotes.Where(v => v.UserId == userId && ids.Contains(v.InsightId)).Select(v => v.InsightId).ToListAsync(ct)).ToHashSet();
        var flagged = (await db.Flags.Where(f => f.TargetKind == InsightKind && f.UserId == userId && ids.Contains(f.TargetId))
            .Select(f => f.TargetId).ToListAsync(ct)).ToHashSet();

        return TypedResults.Ok(new CommunityInsightsDto(total, rows
            .Select(r => ToDto(r.Insight, r.Helpful, voted.Contains(r.Insight.Id), userId, flagged.Contains(r.Insight.Id)))
            .ToList()));
    }

    private static async Task<Results<Created<CommunityInsightDto>, ValidationProblem, ProblemHttpResult>> PostInsightAsync(
        Guid showId, PostInsightRequest request, ClaimsPrincipal user, RatingsContext db, Moderation moderation, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? "";
        if (text.Length is < InsightMinLength or > InsightMaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["text"] = [$"An insight is {InsightMinLength} to {InsightMaxLength} characters."]
            });
        }
        if (CheckPost(text, moderation) is { } refused)
        {
            return refused;
        }
        var userId = user.GetUserId();
        var now = DateTime.UtcNow;
        if (await Moderation.TooManyPostsAsync(db, userId, now, ct))
        {
            return TooMany("posts");
        }

        var insight = new CommunityInsight
        {
            Id = Guid.CreateVersion7(),
            ShowId = showId,
            EpisodeId = request.EpisodeId,
            UserId = userId,
            DisplayName = user.GetDisplayName() ?? "Viewer",
            Text = text,
            IsSpoiler = request.IsSpoiler,
            HiddenReason = moderation.Check(text) == TextVerdict.Held ? HiddenReasons.Held : null,
            CreatedAtUtc = now
        };
        db.Insights.Add(insight);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/community-insights/{insight.Id}", ToDto(insight, 0, false, userId, false));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteInsightAsync(Guid id, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await db.Insights.Where(i => i.Id == id && i.UserId == userId).ExecuteDeleteAsync(ct) == 0)
        {
            return TypedResults.NotFound();
        }
        await db.InsightVotes.Where(v => v.InsightId == id).ExecuteDeleteAsync(ct);
        await db.Flags.Where(f => f.TargetKind == InsightKind && f.TargetId == id).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> VoteAsync(Guid id, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await db.Insights.FindAsync([id], ct) is not { HiddenReason: null } insight)
        {
            return TypedResults.NotFound();
        }
        if (insight.UserId == userId)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["You can't mark your own insight helpful."] });
        }
        if (!await db.InsightVotes.AnyAsync(v => v.InsightId == id && v.UserId == userId, ct))
        {
            db.InsightVotes.Add(new InsightVote { InsightId = id, UserId = userId, CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UnvoteAsync(Guid id, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await db.InsightVotes.Where(v => v.InsightId == id && v.UserId == userId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// A suggested correction. A second suggestion from the same person for the same field, while the first is still
    /// pending, replaces it rather than queueing twice.
    /// </summary>
    private static async Task<Results<Ok<CorrectionDto>, ValidationProblem, ProblemHttpResult>> SuggestAsync(
        SuggestCorrectionRequest request, ClaimsPrincipal user, RatingsContext db, Moderation moderation, CancellationToken ct)
    {
        var value = request.SuggestedValue?.Trim() ?? "";
        if (ValidateCorrection(request.Field, request.EpisodeId, value) is { } error)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["suggestedValue"] = [error] });
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 1000)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["A note is at most 1000 characters."] });
        }
        if (CheckPost($"{value} {note}", moderation) is { } refused)
        {
            return refused;
        }

        var userId = user.GetUserId();
        var now = DateTime.UtcNow;
        var correction = await db.Corrections.FirstOrDefaultAsync(c => c.UserId == userId && c.Status == "pending"
            && c.ShowId == request.ShowId && c.EpisodeId == request.EpisodeId && c.Field == request.Field, ct);
        if (correction is null)
        {
            if (await Moderation.TooManyPostsAsync(db, userId, now, ct))
            {
                return TooMany("posts");
            }
            correction = new Correction
            {
                Id = Guid.CreateVersion7(),
                ShowId = request.ShowId,
                EpisodeId = request.EpisodeId,
                Field = request.Field,
                SuggestedValue = value,
                UserId = userId,
                DisplayName = user.GetDisplayName() ?? "Viewer",
                Status = "pending",
                CreatedAtUtc = now
            };
            db.Corrections.Add(correction);
        }
        correction.SuggestedValue = value;
        correction.CurrentValue = Truncate(request.CurrentValue, 4000);
        correction.Note = note;
        correction.Context = Truncate(request.Context, 300);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(correction));
    }

    private static async Task<Ok<List<CorrectionDto>>> GetMyCorrectionsAsync(ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var mine = await db.Corrections.AsNoTracking().Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAtUtc).Take(50).ToListAsync(ct);
        return TypedResults.Ok(mine.Select(ToDto).ToList());
    }

    /// <summary>The same rules the catalog sync worker applies, checked here so the suggester hears about them.</summary>
    public static string? ValidateCorrection(string field, Guid? episodeId, string value) => field switch
    {
        _ when !CorrectionFields.Contains(field) => $"Use one of: {string.Join(", ", CorrectionFields)}.",
        "show-summary" when episodeId is not null => "A show's summary belongs to the show, not an episode.",
        "episode-title" or "episode-summary" or "episode-airdate" or "episode-runtime" when episodeId is null
            => "That field belongs to an episode.",
        "episode-airdate" when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            => "An air date is yyyy-MM-dd.",
        "episode-runtime" when !int.TryParse(value, out var minutes) || minutes is < 1 or > 600 => "A runtime is 1 to 600 minutes.",
        "episode-title" when value.Length is 0 or > 300 => "A title is 1 to 300 characters.",
        _ when value.Length is 0 or > 4000 => "A suggestion is 1 to 4000 characters.",
        _ => null
    };

    public static CorrectionDto ToDto(Correction c) =>
        new(c.Id, c.ShowId, c.EpisodeId, c.Field, c.CurrentValue, c.SuggestedValue, c.Note, c.Context, c.DisplayName, c.Status,
            c.ReviewedBy, c.ReviewedAtUtc, c.CreatedAtUtc);

    private static CommunityInsightDto ToDto(CommunityInsight i, int helpful, bool voted, string userId, bool flagged) =>
        new(i.Id, i.ShowId, i.EpisodeId, i.DisplayName, i.Text, i.IsSpoiler, helpful, voted, i.UserId == userId, flagged,
            i.UserId == userId ? i.HiddenReason : null, i.CreatedAtUtc);

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(max, text.Trim().Length)];
}
