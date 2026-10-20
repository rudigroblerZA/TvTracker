using Microsoft.EntityFrameworkCore;
using Recommendations.API.Data;

namespace Recommendations.API.Services;

public record ScoredShow(Guid ShowId, double Score, string Reason);

/// <summary>
/// Phase 1 scoring: the provider's similar-shows list plus overlap of genres, keywords and cast.
/// Phase 2 adds collaborative filtering ("people who follow X also follow Y") from follow signals.
/// </summary>
public sealed class RecommendationEngine(RecommendationsContext db)
{
    /// <summary>Shows similar to <paramref name="showId"/>, best first.</summary>
    public async Task<List<ScoredShow>> GetSimilarAsync(Guid showId, int limit, CancellationToken ct)
    {
        var target = await db.Shows.AsNoTracking().SingleOrDefaultAsync(s => s.ShowId == showId, ct);
        if (target is null)
        {
            return [];
        }

        var candidates = await CandidatesAsync(target, ct);

        return candidates
            .Where(c => c.ShowId != showId)
            .Select(c => new ScoredShow(c.ShowId, Similarity(target, c), ReasonFor(target, c)))
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .Take(limit)
            .ToList();
    }

    /// <summary>Personalised picks for a user, excluding shows they follow or dismissed.</summary>
    public async Task<List<ScoredShow>> ComputeForUserAsync(string userId, int limit, CancellationToken ct)
    {
        var signals = await db.Signals.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);
        var dismissed = (await db.Dismissals.Where(d => d.UserId == userId).Select(d => d.ShowId).ToListAsync(ct)).ToHashSet();
        var engaged = signals.Where(s => s.Followed || s.EpisodesWatched > 0).Select(s => s.ShowId).ToHashSet();

        var allShows = await db.Shows.AsNoTracking().ToListAsync(ct);
        var showsById = allShows.ToDictionary(s => s.ShowId);
        var candidates = allShows.Where(s => !engaged.Contains(s.ShowId) && !dismissed.Contains(s.ShowId)).ToList();

        if (signals.Count == 0)
        {
            // Cold start: what's popular.
            return candidates
                .OrderByDescending(s => s.Popularity)
                .Take(limit)
                .Select(s => new ScoredShow(s.ShowId, s.Popularity / 100, "Popular right now"))
                .ToList();
        }

        // Taste profile: how much the user likes each genre and keyword, weighted by engagement.
        var genreWeights = new Dictionary<string, double>();
        var keywordWeights = new Dictionary<string, double>();
        var anchors = new List<(ShowFeatures Show, double Weight)>();

        foreach (var signal in signals)
        {
            if (!showsById.TryGetValue(signal.ShowId, out var show))
            {
                continue;
            }

            var weight = Weight(signal);
            if (weight <= 0)
            {
                continue;
            }

            anchors.Add((show, weight));
            foreach (var g in show.Genres)
            {
                genreWeights[g] = genreWeights.GetValueOrDefault(g) + weight;
            }
            foreach (var k in show.Keywords)
            {
                keywordWeights[k] = keywordWeights.GetValueOrDefault(k) + weight;
            }
        }

        var maxGenre = genreWeights.Values.DefaultIfEmpty(1).Max();
        var maxKeyword = keywordWeights.Values.DefaultIfEmpty(1).Max();

        var coFollows = await CoFollowCountsAsync(userId, engaged, candidates.Select(c => c.ShowId).ToList(), ct);
        var maxCoFollow = coFollows.Values.DefaultIfEmpty(0).Max();

        var scored = new List<ScoredShow>();
        foreach (var candidate in candidates)
        {
            var genreScore = candidate.Genres.Count == 0 ? 0 : candidate.Genres.Sum(g => genreWeights.GetValueOrDefault(g)) / (maxGenre * candidate.Genres.Count);
            var keywordScore = candidate.Keywords.Count == 0 ? 0 : candidate.Keywords.Sum(k => keywordWeights.GetValueOrDefault(k)) / (maxKeyword * candidate.Keywords.Count);
            var coFollowScore = maxCoFollow == 0 ? 0 : (double)coFollows.GetValueOrDefault(candidate.ShowId) / maxCoFollow;

            // The followed show this candidate is most similar to, for the "Because you watch X" reason.
            var best = anchors
                .Select(a => (a.Show, Score: Similarity(a.Show, candidate) * a.Weight))
                .OrderByDescending(a => a.Score)
                .FirstOrDefault();

            var score = 0.35 * genreScore + 0.2 * keywordScore + 0.25 * coFollowScore
                + 0.15 * Math.Min(1, best.Score) + 0.05 * candidate.Popularity / 100;

            if (score <= 0)
            {
                continue;
            }

            var reason = best.Show is not null && best.Score > 0.2
                ? $"Because you watch {best.Show.Title}"
                : coFollowScore > 0.5
                    ? "Popular with people who watch what you watch"
                    : candidate.Genres.OrderByDescending(g => genreWeights.GetValueOrDefault(g)).Select(g => $"Because you like {g}").FirstOrDefault()
                      ?? "Recommended for you";

            scored.Add(new ScoredShow(candidate.ShowId, Math.Round(score, 4), reason));
        }

        return scored.OrderByDescending(s => s.Score).Take(limit).ToList();
    }

    /// <summary>For each candidate: how many other users who follow one of the user's shows also follow the candidate.</summary>
    private async Task<Dictionary<Guid, int>> CoFollowCountsAsync(string userId, HashSet<Guid> myShows, List<Guid> candidateIds, CancellationToken ct)
    {
        if (myShows.Count == 0 || candidateIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var myShowIds = myShows.ToList();
        var similarUsers = db.Signals
            .Where(s => s.Followed && s.UserId != userId && myShowIds.Contains(s.ShowId))
            .Select(s => s.UserId)
            .Distinct();

        return await db.Signals
            .Where(s => s.Followed && candidateIds.Contains(s.ShowId) && similarUsers.Contains(s.UserId))
            .GroupBy(s => s.ShowId)
            .Select(g => new { ShowId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ShowId, x => x.Count, ct);
    }

    private async Task<List<ShowFeatures>> CandidatesAsync(ShowFeatures target, CancellationToken ct)
    {
        var similarIds = target.SimilarShowIds;
        var genres = target.Genres;

        // Shows sharing a genre, plus the provider's similar shows.
        return await db.Shows.AsNoTracking()
            .Where(s => similarIds.Contains(s.ShowId) || s.Genres.Any(g => genres.Contains(g)))
            .Take(500)
            .ToListAsync(ct);
    }

    private static double Weight(UserShowSignal s)
    {
        var weight = (s.Followed ? 1.0 : 0) + Math.Min(s.EpisodesWatched, 20) / 10.0;
        if (s.Rating is { } rating)
        {
            // 5 stars doubles the weight; 1 star makes it negative (excluded).
            weight *= (rating - 2) / 1.5;
        }
        return weight;
    }

    private static double Similarity(ShowFeatures a, ShowFeatures b)
    {
        var providerRank = a.SimilarShowIds.IndexOf(b.ShowId);
        var providerScore = providerRank < 0 ? 0 : 1.0 - providerRank * 0.1;

        return 0.4 * Math.Max(0, providerScore)
            + 0.3 * Jaccard(a.Genres, b.Genres)
            + 0.2 * Jaccard(a.Keywords, b.Keywords)
            + 0.1 * Jaccard(a.CastNames, b.CastNames);
    }

    private static string ReasonFor(ShowFeatures target, ShowFeatures candidate)
    {
        if (target.SimilarShowIds.Contains(candidate.ShowId))
        {
            return $"Often watched with {target.Title}";
        }

        var shared = candidate.Genres.Intersect(target.Genres).ToList();
        return shared.Count > 0 ? $"Also {string.Join(" and ", shared.Take(2)).ToLowerInvariant()}" : "Similar themes";
    }

    private static double Jaccard(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var setA = a.Select(x => x.ToLowerInvariant()).ToHashSet();
        var setB = b.Select(x => x.ToLowerInvariant()).ToHashSet();
        var intersection = setA.Count(setB.Contains);
        return (double)intersection / (setA.Count + setB.Count - intersection);
    }
}
