using Npgsql;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using Ratings.API.Services;
using TvTracker;

namespace Ratings.API.Apis;

/// <param name="PersonId">The cast member, as the catalog knows them.</param>
public record CharacterVoteRequest(Guid PersonId);

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
    public static IEnumerable<CharacterVoteCountDto> Ranked(IEnumerable<CharacterVoteCountDto> counts) =>
        counts.Where(c => c.Votes > 0).OrderByDescending(c => c.Votes).ThenBy(c => c.PersonId);

    internal static async Task<Ok<ShowCharacterVotesDto>> GetShowVotesAsync(Guid showId, RatingsContext db, CatalogLookup catalog, CancellationToken ct)
    {
        var counts = await db.CharacterVotes
            .Where(v => v.ShowId == showId)
            .GroupBy(v => v.PersonId)
            .Select(g => new CharacterVoteCountDto(g.Key, g.Count()))
            .ToListAsync(ct);
        // The places go to people in the cast as it is now, where the catalog can say: someone who has left it, or an
        // id let through while the catalog couldn't be asked, would be dropped by the page and leave a gap. With no
        // votes there is nothing to place and the catalog isn't asked: this is open to anyone, with any id.
        var inCast = counts.Count == 0 ? null : await catalog.CastOfAsync(showId, ct);
        var top = Ranked(counts).Where(c => inCast is null || inCast.Contains(c.PersonId)).Take(TopOfShow).ToList();
        return TypedResults.Ok(new ShowCharacterVotesDto(counts.Sum(c => c.Votes), top));
    }

    internal static async Task<Ok<EpisodeCharacterVotesDto>> GetEpisodeVotesAsync(
        Guid episodeId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        return TypedResults.Ok(await WatchedAsync(db, userId, episodeId, ct) ? await VotesAsync(db, userId, episodeId, ct) : Locked);
    }

    /// <summary>
    /// What a reader who hasn't watched the episode gets.
    /// </summary>
    private static readonly EpisodeCharacterVotesDto Locked = new(true, 0, [], null);

    /// <summary>
    /// An episode's votes as someone who has watched it sees them, with their own pick. Votes answer with this too,
    /// so the page needn't ask again.
    /// </summary>
    private static async Task<EpisodeCharacterVotesDto> VotesAsync(RatingsContext db, string userId, Guid episodeId, CancellationToken ct)
    {
        var people = await db.CharacterVotes
            .Where(v => v.EpisodeId == episodeId)
            .GroupBy(v => v.PersonId)
            .Select(g => new { PersonId = g.Key, Votes = g.Count(), Mine = g.Any(v => v.UserId == userId) })
            .ToListAsync(ct);
        return new EpisodeCharacterVotesDto(false, people.Sum(p => p.Votes),
            Ranked(people.Select(p => new CharacterVoteCountDto(p.PersonId, p.Votes))).ToList(),
            people.Where(p => p.Mine).Select(p => (Guid?)p.PersonId).FirstOrDefault());
    }

    private const string NotWatched = "Mark the episode watched before voting on it.";

    internal static async Task<Results<Ok<EpisodeCharacterVotesDto>, ValidationProblem, Conflict<string>>> VoteAsync(
        Guid episodeId, CharacterVoteRequest request, ClaimsPrincipal user, RatingsContext db, CatalogLookup catalog, CancellationToken ct)
    {
        if (request.PersonId == Guid.Empty)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["personId"] = ["Say which cast member."]
            });
        }

        var userId = user.GetUserId();
        var watched = await db.WatchedEpisodes.Where(w => w.UserId == userId && w.EpisodeId == episodeId).Select(w => new { w.ShowId }).FirstOrDefaultAsync(ct);
        if (watched is null)
        {
            return TypedResults.Conflict(NotWatched);
        }

        // The show is the one Tracking says the episode belongs to, or the catalog for a watch from before the copy
        // kept its show; never one the request names, so a vote can't be counted towards another show's favourites.
        var showId = watched.ShowId ?? await catalog.ShowOfAsync(episodeId, ct);
        if (showId is null)
        {
            return TypedResults.Conflict("Couldn't tell which show the episode belongs to. Try again in a moment.");
        }
        // Only someone in the show's cast, where the catalog can say: an id from anywhere else would count in the show's
        // total and never be shown.
        if (await catalog.InCastAsync(showId.Value, request.PersonId, ct) == false)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["personId"] = ["Pick someone from the show's cast."]
            });
        }
        try
        {
            // One statement, so two votes at once from the same viewer can't both add the row: the later pick wins.
            await db.Database.ExecuteSqlAsync($"""
                insert into character_votes ("UserId", "EpisodeId", "ShowId", "PersonId", "CreatedAtUtc") values ({userId}, {episodeId}, {showId.Value}, {request.PersonId}, {DateTime.UtcNow})
                on conflict ("UserId", "EpisodeId") do update set "ShowId" = excluded."ShowId", "PersonId" = excluded."PersonId", "CreatedAtUtc" = excluded."CreatedAtUtc"
                """, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // The vote belongs to the watch (see RatingsContext): the episode was unwatched while this was on its way.
            return TypedResults.Conflict(NotWatched);
        }
        return TypedResults.Ok(await VotesAsync(db, userId, episodeId, ct));
    }

    internal static async Task<Ok<EpisodeCharacterVotesDto>> RemoveVoteAsync(Guid episodeId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await db.CharacterVotes.Where(v => v.UserId == userId && v.EpisodeId == episodeId).ExecuteDeleteAsync(ct);
        return TypedResults.Ok(await WatchedAsync(db, userId, episodeId, ct) ? await VotesAsync(db, userId, episodeId, ct) : Locked);
    }

    private static Task<bool> WatchedAsync(RatingsContext db, string userId, Guid episodeId, CancellationToken ct) =>
        db.WatchedEpisodes.AnyAsync(w => w.UserId == userId && w.EpisodeId == episodeId, ct);
}
