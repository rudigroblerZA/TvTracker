using Microsoft.EntityFrameworkCore;
using Recommendations.API.Data;
using TvTracker.IntegrationEvents;

namespace Recommendations.API.Services;

public record ScoredShow(Guid ShowId, double Score, string Reason);

/// <summary>
/// Scoring blends three things:
/// <list type="bullet">
/// <item><b>Content:</b> the provider's similar-shows list plus overlap of genres, keywords and cast.</item>
/// <item><b>Nanogenres:</b> sharing one of the catalog's discovered nanogenres ("Sports comedy").</item>
/// <item><b>DNA:</b> how alike two shows are made and play out, from Analytics' show DNA (<see cref="DnaTraits"/>).</item>
/// <item><b>Co-follows:</b> "people who follow X also follow Y".</item>
/// </list>
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
        var extras = await ExtrasAsync(candidates.Select(c => c.ShowId).Append(showId), ct);

        return candidates
            .Where(c => c.ShowId != showId)
            .Select(c => (Show: c, Match: Match(target, c, extras)))
            .Select(x => new ScoredShow(x.Show.ShowId, Math.Round(x.Match.Score, 4),
                x.Match.SharedNanogenre is { } nanogenre ? $"Also {Article(nanogenre)} {nanogenre.ToLowerInvariant()}"
                : x.Match.DnaLed ? $"Same DNA: {x.Match.SharedTraits}"
                : ReasonFor(target, x.Show)))
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
        var extras = await ExtrasAsync(candidates.Select(c => c.ShowId).Concat(anchors.Select(a => a.Show.ShowId)), ct);
        var maxCoFollow = coFollows.Values.DefaultIfEmpty(0).Max();

        var scored = new List<ScoredShow>();
        foreach (var candidate in candidates)
        {
            var genreScore = candidate.Genres.Count == 0 ? 0 : candidate.Genres.Sum(g => genreWeights.GetValueOrDefault(g)) / (maxGenre * candidate.Genres.Count);
            var keywordScore = candidate.Keywords.Count == 0 ? 0 : candidate.Keywords.Sum(k => keywordWeights.GetValueOrDefault(k)) / (maxKeyword * candidate.Keywords.Count);
            var coFollowScore = maxCoFollow == 0 ? 0 : (double)coFollows.GetValueOrDefault(candidate.ShowId) / maxCoFollow;

            // The followed show this candidate is most similar to, for the "Because you watch X" reason.
            var best = anchors
                .Select(a => (a.Show, Match: Match(a.Show, candidate, extras), a.Weight))
                .Select(a => (a.Show, a.Match, Score: a.Match.Score * a.Weight))
                .OrderByDescending(a => a.Score)
                .FirstOrDefault();

            var score = 0.3 * genreScore + 0.15 * keywordScore + 0.2 * coFollowScore
                + 0.3 * Math.Min(1, best.Score) + 0.05 * candidate.Popularity / 100;

            if (score <= 0)
            {
                continue;
            }

            var reason = best.Show is not null && best.Score > 0.2
                ? best.Match.SharedNanogenre is { } nanogenre ? $"Another {nanogenre.ToLowerInvariant()}, like {best.Show.Title}"
                    : best.Match.DnaLed ? $"Same DNA as {best.Show.Title}: {best.Match.SharedTraits}"
                    : $"Because you watch {best.Show.Title}"
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

    /// <param name="Score">0-1: content similarity, scaled by DNA affinity.</param>
    /// <param name="DnaLed">The DNAs are close and share distinctive traits, so they make the better reason.</param>
    /// <param name="SharedTraits">Distinctive traits both shows have, strongest first ("drops all at once, big finales").</param>
    /// <param name="SharedNanogenre">The most specific nanogenre both shows are in ("Sports comedy"), the plainest reason there is.</param>
    private sealed record ShowMatch(double Score, bool DnaLed, string? SharedTraits, string? SharedNanogenre = null);

    /// <summary>A show's DNA and nanogenres, by show.</summary>
    private sealed record Extras(Dictionary<Guid, Dictionary<string, double>> Dna, Dictionary<Guid, List<string>> Nanogenres);

    /// <summary>
    /// How much of the content score shared nanogenres make up: a nanogenre is a whole group of shows like each other
    /// (discovered by the catalog), so sharing one says more than overlapping genres or keywords.
    /// </summary>
    private const double NanogenreShare = 0.25;

    /// <summary>
    /// DNA scales the content score between 1 − <see cref="DnaShare"/> (nothing alike) and 1 (alike): it reorders
    /// shows that are already about similar things, and never makes a show about something else similar. Two shows
    /// without DNA to compare get a middling affinity, so they aren't favoured or punished for it.
    /// </summary>
    private const double DnaShare = 0.5;

    private const double UnknownAffinity = 0.5;

    /// <summary>A reason names the DNA from this affinity, when the shows also share a distinctive trait.</summary>
    private const double DnaReasonAffinity = 0.6;


    /// <summary>
    /// DNA similarity bunches between 0.6 and 0.95 (most shows share some form), so it's rescaled to an affinity:
    /// <see cref="DnaFloor"/> and below is none, <see cref="DnaCeiling"/> and above full.
    /// </summary>
    private const double DnaFloor = 0.65;
    private const double DnaCeiling = 0.95;

    private static readonly HashSet<string> Kinds =
        ["scripted", "animation", "reality", "documentary", "talk show", "variety", "game show", "news", "sports", "award show", "panel show"];

    private static ShowMatch Match(ShowFeatures a, ShowFeatures b, Extras extras)
    {
        var nanogenresA = extras.Nanogenres.GetValueOrDefault(a.ShowId) ?? [];
        var nanogenresB = extras.Nanogenres.GetValueOrDefault(b.ShowId) ?? [];
        var sharedNanogenre = nanogenresA.FirstOrDefault(nanogenresB.Contains);
        var content = (1 - NanogenreShare) * Similarity(a, b) + NanogenreShare * Jaccard(nanogenresA, nanogenresB);

        var dna = extras.Dna;
        if (!dna.TryGetValue(a.ShowId, out var dnaA) || !dna.TryGetValue(b.ShowId, out var dnaB)
            || DnaTraits.Similarity(dnaA, dnaB) is not { } similarity)
        {
            return new ShowMatch(content * (1 - DnaShare + DnaShare * UnknownAffinity), false, null, sharedNanogenre);
        }

        // DNA describes form, not subject: a reality show and a drama can be made alike, so a different kind of show
        // (from the catalog's keywords: its type and language) weakens the match a lot, and another language a little.
        var affinity = Math.Clamp((similarity - DnaFloor) / (DnaCeiling - DnaFloor), 0, 1);
        if (KindOf(a) != KindOf(b))
        {
            affinity *= 0.4;
        }
        if (!a.Keywords.Where(k => !Kinds.Contains(k)).Intersect(b.Keywords.Where(k => !Kinds.Contains(k))).Any())
        {
            affinity *= 0.8;
        }

        // Traits both shows have, and that set both apart from the typical show, most distinctive first.
        var shared = DnaTraits.All
            .Where(t => dnaA.ContainsKey(t.Key) && dnaB.ContainsKey(t.Key))
            .Select(t => (Label: DnaTraits.LabelFor(t.Key, dnaA[t.Key]), Other: DnaTraits.LabelFor(t.Key, dnaB[t.Key]),
                Distinctiveness: Math.Min(DnaTraits.Distinctiveness(t.Key, dnaA[t.Key]), DnaTraits.Distinctiveness(t.Key, dnaB[t.Key])),
                t.Weight))
            .Where(t => t.Label == t.Other && t.Distinctiveness > 0)
            .OrderByDescending(t => t.Weight * t.Distinctiveness)
            .Take(2)
            .Select(t => t.Label!.ToLowerInvariant())
            .ToList();

        return new ShowMatch(content * (1 - DnaShare + DnaShare * affinity),
            affinity >= DnaReasonAffinity && shared.Count > 0,
            shared.Count == 0 ? null : string.Join(", ", shared),
            sharedNanogenre);
    }

    /// <summary>"a sports comedy", "an animated family comedy".</summary>
    private static string Article(string name) => "aeiouAEIOU".Contains(name[0]) ? "an" : "a";


    private static string? KindOf(ShowFeatures show) => show.Keywords.FirstOrDefault(Kinds.Contains);

    private async Task<Extras> ExtrasAsync(IEnumerable<Guid> showIds, CancellationToken ct)
    {
        var ids = showIds.Distinct().ToList();
        return new Extras(
            await db.Dna.AsNoTracking().Where(d => ids.Contains(d.ShowId)).ToDictionaryAsync(d => d.ShowId, d => d.Traits, ct),
            await db.Nanogenres.AsNoTracking().Where(n => ids.Contains(n.ShowId)).ToDictionaryAsync(n => n.ShowId, n => n.Names, ct));
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
