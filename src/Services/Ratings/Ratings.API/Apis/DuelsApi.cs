using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using System.Security.Claims;
using TvTracker;

namespace Ratings.API.Apis;

/// <param name="Watched">Episodes of the show the user has watched: what can be duelled.</param>
/// <param name="Duels">Answers given so far.</param>
/// <param name="Pair">The next two episodes to compare; null with fewer than two watched.</param>
/// <param name="Ranking">Episodes that have been in a duel, best first.</param>
public record DuelStateDto(int Watched, int Duels, DuelPairDto? Pair, IReadOnlyList<DuelRankDto> Ranking);

public record DuelPairDto(Guid First, Guid Second);

/// <param name="Score">0-100: the chance it beats an average episode of the show, by the user's own answers.</param>
public record DuelRankDto(Guid EpisodeId, int Score, int Wins, int Losses);

public record DuelRequest(Guid WinnerId, Guid LoserId);

/// <summary>
/// Episode duels: the user ranks a show's episodes they've watched by picking the better of two, which is easier and
/// more telling than stars (most episodes would get 4).
/// <list type="bullet">
/// <item><b>Ranking:</b> a Bradley-Terry model over the user's answers, fitted by the MM algorithm, with every episode
/// also given one win and one loss against an average episode. That prior keeps an episode that won its only duel
/// from topping one that won ten of twelve, and makes the order independent of when answers were given.</item>
/// <item><b>Pairs:</b> the episode in the fewest duels, against one it hasn't met whose score is closest (one of the three
/// closest, at random): close calls are what a ranking still needs to learn.</item>
/// <item><b>Only watched episodes</b> from Ratings' copy of Tracking's watches, so neither a pair nor an answer can be
/// about an episode the user hasn't seen. Unwatching one drops it from the ranking, not its answers.</item>
/// </list>
/// </summary>
public static class DuelsApi
{
    private const int Iterations = 60;

    public static IEndpointRouteBuilder MapDuelsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/shows/{showId:guid}/duels").RequireAuthorization().RequireFeature(FeatureFlags.EpisodeDuels);
        api.MapGet("", GetAsync);
        api.MapPost("", DecideAsync);
        api.MapDelete("", ResetAsync);
        return app;
    }

    private static async Task<Ok<DuelStateDto>> GetAsync(Guid showId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct) =>
        TypedResults.Ok(await StateAsync(db, user.GetUserId(), showId, ct));

    private static async Task<Results<Ok<DuelStateDto>, ValidationProblem>> DecideAsync(
        Guid showId, DuelRequest request, ClaimsPrincipal user, RatingsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var ids = new[] { request.WinnerId, request.LoserId };
        var watched = await db.WatchedEpisodes
            .CountAsync(w => w.UserId == userId && w.ShowId == showId && ids.Contains(w.EpisodeId), ct);
        if (request.WinnerId == request.LoserId || watched < 2)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["episodes"] = ["Pick between two different episodes of this show that you've watched."]
            });
        }

        db.Duels.Add(new EpisodeDuel
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ShowId = showId,
            WinnerId = request.WinnerId,
            LoserId = request.LoserId,
            DecidedAtUtc = time.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await StateAsync(db, userId, showId, ct));
    }

    private static async Task<NoContent> ResetAsync(Guid showId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await db.Duels.Where(d => d.UserId == userId && d.ShowId == showId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<DuelStateDto> StateAsync(RatingsContext db, string userId, Guid showId, CancellationToken ct)
    {
        var episodes = await db.WatchedEpisodes.AsNoTracking()
            .Where(w => w.UserId == userId && w.ShowId == showId)
            .Select(w => w.EpisodeId)
            .ToListAsync(ct);
        var watched = episodes.ToHashSet();
        var duels = (await db.Duels.AsNoTracking()
                .Where(d => d.UserId == userId && d.ShowId == showId)
                .Select(d => new { d.WinnerId, d.LoserId })
                .ToListAsync(ct))
            .Where(d => watched.Contains(d.WinnerId) && watched.Contains(d.LoserId))
            .Select(d => (d.WinnerId, d.LoserId))
            .ToList();

        var strength = Fit(episodes, duels);
        var wins = duels.GroupBy(d => d.WinnerId).ToDictionary(g => g.Key, g => g.Count());
        var losses = duels.GroupBy(d => d.LoserId).ToDictionary(g => g.Key, g => g.Count());
        var ranking = episodes
            .Where(id => wins.ContainsKey(id) || losses.ContainsKey(id))
            .OrderByDescending(id => strength[id])
            .Select(id => new DuelRankDto(id, (int)Math.Round(100 * strength[id] / (strength[id] + 1)),
                wins.GetValueOrDefault(id), losses.GetValueOrDefault(id)))
            .ToList();

        return new DuelStateDto(episodes.Count, duels.Count, NextPair(episodes, duels, strength, wins, losses), ranking);
    }

    /// <summary>
    /// Bradley-Terry strengths (an average episode is 1), with one virtual win and loss against it each. MM update:
    /// s = (wins + 1) / (Σ games / (s + s_opponent) + 2 / (s + 1)).
    /// </summary>
    internal static Dictionary<Guid, double> Fit(IReadOnlyList<Guid> episodes, IReadOnlyList<(Guid Winner, Guid Loser)> duels)
    {
        var strength = episodes.ToDictionary(id => id, _ => 1.0);
        var wins = duels.GroupBy(d => d.Winner).ToDictionary(g => g.Key, g => g.Count());
        var games = duels.SelectMany(d => new[] { (d.Winner, d.Loser), (d.Loser, d.Winner) })
            .ToLookup(g => g.Item1, g => g.Item2);
        for (var i = 0; i < Iterations; i++)
        {
            var next = new Dictionary<Guid, double>(strength.Count);
            foreach (var (id, s) in strength)
            {
                var denominator = games[id].Sum(other => 1 / (s + strength[other])) + 2 / (s + 1);
                next[id] = (wins.GetValueOrDefault(id) + 1) / denominator;
            }
            strength = next;
        }
        return strength;
    }

    private static DuelPairDto? NextPair(List<Guid> episodes, List<(Guid WinnerId, Guid LoserId)> duels, Dictionary<Guid, double> strength,
        Dictionary<Guid, int> wins, Dictionary<Guid, int> losses)
    {
        if (episodes.Count < 2)
        {
            return null;
        }
        var met = duels.SelectMany(d => new[] { (d.WinnerId, d.LoserId), (d.LoserId, d.WinnerId) }).ToHashSet();
        int Played(Guid id) => wins.GetValueOrDefault(id) + losses.GetValueOrDefault(id);

        // The least-played episode that still has someone left to meet; ties broken at random.
        foreach (var first in episodes.OrderBy(Played).ThenBy(_ => Random.Shared.Next()))
        {
            var options = episodes.Where(other => other != first && !met.Contains((first, other)))
                .OrderBy(other => Math.Abs(Math.Log(strength[other]) - Math.Log(strength[first])))
                .Take(3)
                .ToList();
            if (options.Count > 0)
            {
                return Random.Shared.Next(2) == 0
                    ? new DuelPairDto(first, options[Random.Shared.Next(options.Count)])
                    : new DuelPairDto(options[Random.Shared.Next(options.Count)], first);
            }
        }

        // Every pair has met: ask again about two at random.
        var shuffled = episodes.OrderBy(_ => Random.Shared.Next()).Take(2).ToList();
        return new DuelPairDto(shuffled[0], shuffled[1]);
    }
}
