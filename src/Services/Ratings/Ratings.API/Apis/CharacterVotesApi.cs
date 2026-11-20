using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using TvTracker;

namespace Ratings.API.Apis;

/// <param name="PersonId">The cast member, as the catalog knows them.</param>
public record CharacterVoteRequest(Guid ShowId, Guid PersonId);

public record CharacterVoteCountDto(Guid PersonId, int Votes);

/// <param name="Locked">The reader hasn't watched the episode: no counts, since who stood out gives it away.</param>
/// <param name="Mine">The cast member the reader voted for.</param>
public record EpisodeCharacterVotesDto(bool Locked, int Total, IReadOnlyList<CharacterVoteCountDto> Counts, Guid? Mine);

/// <param name="Total">Every vote cast on the show's episodes, not just those for the ones listed.</param>
public record ShowCharacterVotesDto(int Total, IReadOnlyList<CharacterVoteCountDto> Top);

/// <summary>
/// Favourite character: one vote per watched episode for the cast member who stood out, and a show's totals.
/// </summary>
public static class CharacterVotesApi
{
    /// <summary>
    /// How many of a show's cast its totals list.
    /// </summary>
    public const int TopOfShow = 5;

    public static IEndpointRouteBuilder MapCharacterVotesApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireFeature(FeatureFlags.CharacterVoting);

        api.MapGet("/shows/{showId:guid}/character-votes", GetShowVotesAsync);

        var mine = api.MapGroup("").RequireAuthorization();
        mine.MapGet("/episodes/{episodeId:guid}/character-votes", GetEpisodeVotesAsync);
        mine.MapPut("/episodes/{episodeId:guid}/character-vote", VoteAsync);
        mine.MapDelete("/episodes/{episodeId:guid}/character-vote", RemoveVoteAsync);

        return app;
    }

    /// <summary>
    /// Most votes first; ties in a fixed order, so a list doesn't reshuffle between two reads.
    /// </summary>
    public static IReadOnlyList<CharacterVoteCountDto> Ranked(IEnumerable<CharacterVoteCountDto> counts, int? take = null)
    {
        var ranked = counts.Where(c => c.Votes > 0).OrderByDescending(c => c.Votes).ThenBy(c => c.PersonId);
        return (take is { } n ? ranked.Take(n) : ranked).ToList();
    }

    private static async Task<Ok<ShowCharacterVotesDto>> GetShowVotesAsync(Guid showId, RatingsContext db, CancellationToken ct)
    {
        var counts = await db.CharacterVotes
            .Where(v => v.ShowId == showId)
            .GroupBy(v => v.PersonId)
            .Select(g => new CharacterVoteCountDto(g.Key, g.Count()))
            .ToListAsync(ct);
        return TypedResults.Ok(new ShowCharacterVotesDto(counts.Sum(c => c.Votes), Ranked(counts, TopOfShow)));
    }

    private static async Task<Ok<EpisodeCharacterVotesDto>> GetEpisodeVotesAsync(
        Guid episodeId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (!await WatchedAsync(db, userId, episodeId, ct))
        {
            return TypedResults.Ok(new EpisodeCharacterVotesDto(true, 0, [], null));
        }

        var counts = await db.CharacterVotes
            .Where(v => v.EpisodeId == episodeId)
            .GroupBy(v => v.PersonId)
            .Select(g => new CharacterVoteCountDto(g.Key, g.Count()))
            .ToListAsync(ct);
        var mine = await db.CharacterVotes
            .Where(v => v.EpisodeId == episodeId && v.UserId == userId)
            .Select(v => (Guid?)v.PersonId)
            .SingleOrDefaultAsync(ct);
        return TypedResults.Ok(new EpisodeCharacterVotesDto(false, counts.Sum(c => c.Votes), Ranked(counts), mine));
    }

    private static async Task<Results<NoContent, ValidationProblem, Conflict<string>>> VoteAsync(
        Guid episodeId, CharacterVoteRequest request, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        if (request.ShowId == Guid.Empty || request.PersonId == Guid.Empty)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["personId"] = ["Say which show and which cast member."]
            });
        }

        var userId = user.GetUserId();
        if (!await WatchedAsync(db, userId, episodeId, ct))
        {
            return TypedResults.Conflict("Mark the episode watched before voting on it.");
        }

        var vote = await db.CharacterVotes.FindAsync([userId, episodeId], ct);
        if (vote is null)
        {
            db.CharacterVotes.Add(new CharacterVote
            {
                UserId = userId,
                EpisodeId = episodeId,
                ShowId = request.ShowId,
                PersonId = request.PersonId,
                CreatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            (vote.ShowId, vote.PersonId, vote.CreatedAtUtc) = (request.ShowId, request.PersonId, DateTime.UtcNow);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RemoveVoteAsync(Guid episodeId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await db.CharacterVotes.Where(v => v.UserId == userId && v.EpisodeId == episodeId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static Task<bool> WatchedAsync(RatingsContext db, string userId, Guid episodeId, CancellationToken ct) =>
        db.WatchedEpisodes.AnyAsync(w => w.UserId == userId && w.EpisodeId == episodeId, ct);
}
