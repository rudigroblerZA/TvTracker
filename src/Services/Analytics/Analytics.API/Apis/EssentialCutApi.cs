using System.Security.Claims;
using System.Text.RegularExpressions;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="Level">"light", "lean" or "essentials".</param>
/// <param name="Available">False when the show can't be cut: too short, or too few episodes are rated (see <paramref name="Reason"/>).</param>
/// <param name="Skips">Runs of consecutive skipped episodes, in watching order ("skip S05E21-E31").</param>
/// <param name="Progress">From the user's next unwatched episode; null when they haven't watched any.</param>
public record EssentialCutDto(
    Guid ShowId,
    string Title,
    string Level,
    bool Available,
    string? Reason,
    CutTotalsDto Full,
    CutTotalsDto Cut,
    IReadOnlyList<CutSeasonDto> Seasons,
    IReadOnlyList<SkipRunDto> Skips,
    CutProgressDto? Progress);

public record CutTotalsDto(int Episodes, int Minutes);

public record CutSeasonDto(int Number, int Episodes, int Kept, int MinutesSaved, IReadOnlyList<CutEpisodeDto> Items);

/// <param name="Rating">The rating used (0-10): the provider's, averaged with our users' when enough rated it; null when unrated.</param>
/// <param name="Why">
/// Why it's kept or skipped: "series-premiere", "season-premiere", "season-finale", "series-finale", "two-parter",
/// "best-of-season", "above-average", "unrated" (kept for continuity), or "below-average" (skipped).
/// </param>
public record CutEpisodeDto(Guid EpisodeId, int EpisodeNumber, string Title, double? Rating, bool Keep, string Why, bool Watched);

public record SkipRunDto(int SeasonNumber, int FromEpisode, int ToEpisode, int Episodes, int Minutes, double AverageRating, double SeasonAverage);

/// <param name="NextEpisodeId">The next unwatched episode the cut keeps.</param>
public record CutProgressDto(Guid? NextEpisodeId, int? NextSeason, int? NextEpisode, CutTotalsDto FullRemaining, CutTotalsDto CutRemaining);

/// <summary>
/// Essential cut: a shorter path through a long show, from its episode ratings. Each season keeps its premiere,
/// finale, best episode and both halves of two-parters, and drops its lowest-rated clearly-below-average episodes,
/// up to a share of the season, both set by the level. Cutting per season keeps the story moving through weaker seasons
/// instead of skipping them whole, and an evenly rated season loses little. Computed on request from the user's
/// own watches, so it's current; the ratings are the provider's, averaged with our users' once
/// <see cref="InsightRules.MinRatingsToShow"/> back an episode.
/// </summary>
public static class EssentialCutApi
{
    /// <summary>Shorter shows aren't worth cutting.</summary>
    public const int MinEpisodes = 20;

    /// <summary>The share of aired episodes that must be rated for the cut to mean anything.</summary>
    public const double MinRatedShare = 0.8;

    // Each level skips up to a share of each season, lowest-rated first, among episodes rated at least a margin
    // (in the season's standard deviations) below the season's average. The margin keeps an evenly rated season
    // mostly intact: Breaking Bad loses 10% at "light", One Piece's filler-heavy seasons more.
    private static readonly Dictionary<string, (double Share, double Margin)> Levels = new()
    {
        ["light"] = (0.15, 1.0),
        ["lean"] = (0.3, 0.5),
        ["essentials"] = (0.5, 0.25)
    };

    // The smallest margin (0-10 scale): differences below it are noise, whatever the spread.
    private const double MinMargin = 0.1;

    // "Part 1", "Part Two", "(1)", "Pt. 2": two-parters are kept or skipped together.
    internal static readonly Regex PartPattern = new(@"(\bpart\s*(\d+|one|two|three|i{1,3})\b|\bpt\.?\s*\d\b|\(\s*\d\s*\))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void MapEssentialCutApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/{showId:guid}/essential-cut", GetEssentialCutAsync).RequireFeature(FeatureFlags.EssentialCut);

    private static async Task<Results<Ok<EssentialCutDto>, NotFound, ValidationProblem>> GetEssentialCutAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, string level = "lean", CancellationToken ct = default)
    {
        level = level.ToLowerInvariant();
        if (!Levels.TryGetValue(level, out var settings))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["level"] = [$"Use one of {string.Join(", ", Levels.Keys)}."] });
        }

        var show = await db.Shows.AsNoTracking().SingleOrDefaultAsync(s => s.ShowId == showId, ct);
        if (show is null)
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => e.ShowId == showId && e.SeasonNumber > 0)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .ToListAsync(ct);
        var aired = episodes.Where(e => e.AirsAtUtc <= now).ToList();

        var ours = await db.EpisodeRatings.AsNoTracking()
            .Where(r => r.ShowId == showId)
            .GroupBy(r => r.EpisodeId)
            .Where(g => g.Count() >= InsightRules.MinRatingsToShow)
            .Select(g => new { EpisodeId = g.Key, Average = g.Average(r => r.Stars * 2.0) })
            .ToDictionaryAsync(g => g.EpisodeId, g => g.Average, ct);
        double? Rating(EpisodeCopy e) => (e.ExternalRating, ours.TryGetValue(e.EpisodeId, out var mine) ? mine : (double?)null) switch
        {
            ({ } theirs, { } us) => Math.Round((theirs + us) / 2, 1),
            ({ } theirs, null) => theirs,
            (null, { } us) => Math.Round(us, 1),
            _ => null
        };

        var userId = user.FindFirst("sub")?.Value;
        var watched = userId is null
            ? []
            : (await db.Watches.AsNoTracking().Where(w => w.UserId == userId && w.ShowId == showId).Select(w => w.EpisodeId).ToListAsync(ct)).ToHashSet();

        // Missing runtimes count as the show's typical one, so the hours stay honest.
        var runtimes = aired.Where(e => e.RuntimeMinutes > 0).Select(e => e.RuntimeMinutes!.Value).Order().ToList();
        var typical = runtimes.Count > 0 ? runtimes[runtimes.Count / 2] : 0;
        int Minutes(EpisodeCopy e) => e.RuntimeMinutes is > 0 ? e.RuntimeMinutes.Value : typical;

        var full = new CutTotalsDto(aired.Count, aired.Sum(Minutes));
        var rated = aired.Count(e => Rating(e) is not null);
        string? reason = aired.Count < MinEpisodes
            ? $"It has {aired.Count} episodes; the essential cut is for shows with {MinEpisodes} or more."
            : rated < aired.Count * MinRatedShare
                ? $"Only {rated} of its {aired.Count} episodes are rated, too few to tell the essential ones."
                : null;
        if (reason is not null)
        {
            return TypedResults.Ok(new EssentialCutDto(showId, show.Title, level, false, reason, full, full, [], [], null));
        }

        // A season's last episode is its finale only once nothing more of it is scheduled.
        var unaired = episodes.Where(e => e.AirsAtUtc is null || e.AirsAtUtc > now).Select(e => e.SeasonNumber).ToHashSet();
        var lastSeason = aired.Max(e => e.SeasonNumber);
        var ended = show.Status == TvTracker.IntegrationEvents.ShowStatus.Ended && unaired.Count == 0;

        var decisions = new Dictionary<Guid, (bool Keep, string Why)>();
        foreach (var season in aired.GroupBy(e => e.SeasonNumber))
        {
            var list = season.ToList();
            var ratings = list.Select(Rating).OfType<double>().ToList();
            var average = ratings.DefaultIfEmpty(0).Average();
            var spread = ratings.Count > 1 ? Math.Sqrt(ratings.Average(x => (x - average) * (x - average))) : 0;
            var below = average - Math.Max(MinMargin, settings.Margin * spread);
            var protectedWhy = new Dictionary<Guid, string>
            {
                [list[0].EpisodeId] = season.Key == aired[0].SeasonNumber ? "series-premiere" : "season-premiere"
            };
            if (!unaired.Contains(season.Key) && list.Count > 1)
            {
                protectedWhy[list[^1].EpisodeId] = ended && season.Key == lastSeason ? "series-finale" : "season-finale";
            }
            if (list.Where(e => Rating(e) is not null).MaxBy(Rating) is { } best)
            {
                protectedWhy.TryAdd(best.EpisodeId, "best-of-season");
            }

            // Lowest first among the clearly below-average episodes, up to the level's share of the season.
            var budget = (int)Math.Floor(list.Count * settings.Share);
            var skip = list
                .Where(e => !protectedWhy.ContainsKey(e.EpisodeId) && Rating(e) is { } r && r <= below)
                .OrderBy(e => Rating(e))
                .Take(budget)
                .Select(e => e.EpisodeId)
                .ToHashSet();

            // Two-parters go together: keep both if either half is kept.
            for (var i = 0; i + 1 < list.Count; i++)
            {
                if (PartPattern.IsMatch(list[i].Title) && PartPattern.IsMatch(list[i + 1].Title)
                    && skip.Contains(list[i].EpisodeId) != skip.Contains(list[i + 1].EpisodeId))
                {
                    skip.Remove(list[i].EpisodeId);
                    skip.Remove(list[i + 1].EpisodeId);
                    protectedWhy.TryAdd(list[i].EpisodeId, "two-parter");
                    protectedWhy.TryAdd(list[i + 1].EpisodeId, "two-parter");
                }
            }

            foreach (var e in list)
            {
                decisions[e.EpisodeId] = skip.Contains(e.EpisodeId)
                    ? (false, "below-average")
                    : (true, protectedWhy.GetValueOrDefault(e.EpisodeId) ?? (Rating(e) is null ? "unrated" : "above-average"));
            }
        }

        var seasons = aired.GroupBy(e => e.SeasonNumber)
            .Select(g => new CutSeasonDto(
                g.Key,
                g.Count(),
                g.Count(e => decisions[e.EpisodeId].Keep),
                g.Where(e => !decisions[e.EpisodeId].Keep).Sum(Minutes),
                g.Select(e => new CutEpisodeDto(e.EpisodeId, e.EpisodeNumber, e.Title, Rating(e), decisions[e.EpisodeId].Keep,
                    decisions[e.EpisodeId].Why, watched.Contains(e.EpisodeId))).ToList()))
            .ToList();
        var kept = aired.Where(e => decisions[e.EpisodeId].Keep).ToList();

        return TypedResults.Ok(new EssentialCutDto(
            showId, show.Title, level, true, null,
            full,
            new CutTotalsDto(kept.Count, kept.Sum(Minutes)),
            seasons,
            SkipRuns(aired, decisions, Rating, Minutes),
            Progress(aired, decisions, watched, Minutes)));
    }

    /// <summary>Consecutive skipped episodes within a season, as ranges.</summary>
    private static List<SkipRunDto> SkipRuns(List<EpisodeCopy> aired, Dictionary<Guid, (bool Keep, string Why)> decisions,
        Func<EpisodeCopy, double?> rating, Func<EpisodeCopy, int> minutes)
    {
        var runs = new List<SkipRunDto>();
        foreach (var season in aired.GroupBy(e => e.SeasonNumber))
        {
            var seasonAverage = Math.Round(season.Select(rating).OfType<double>().DefaultIfEmpty(0).Average(), 1);
            var run = new List<EpisodeCopy>();
            foreach (var e in season.Append(null))
            {
                if (e is not null && !decisions[e.EpisodeId].Keep)
                {
                    run.Add(e);
                    continue;
                }
                if (run.Count > 0)
                {
                    runs.Add(new SkipRunDto(season.Key, run[0].EpisodeNumber, run[^1].EpisodeNumber, run.Count, run.Sum(minutes),
                        Math.Round(run.Select(rating).OfType<double>().Average(), 1), seasonAverage));
                    run = [];
                }
            }
        }
        return runs;
    }

    /// <summary>What's left from the user's next unwatched episode, in full and in the cut.</summary>
    private static CutProgressDto? Progress(List<EpisodeCopy> aired, Dictionary<Guid, (bool Keep, string Why)> decisions,
        HashSet<Guid> watched, Func<EpisodeCopy, int> minutes)
    {
        if (watched.Count == 0)
        {
            return null;
        }

        // Everything after the last watched episode (episodes skipped earlier on don't count as still to watch).
        var last = aired.FindLastIndex(e => watched.Contains(e.EpisodeId));
        var remaining = aired.Skip(last + 1).ToList();
        var remainingKept = remaining.Where(e => decisions[e.EpisodeId].Keep).ToList();
        var next = remainingKept.FirstOrDefault();
        return new CutProgressDto(
            next?.EpisodeId, next?.SeasonNumber, next?.EpisodeNumber,
            new CutTotalsDto(remaining.Count, remaining.Sum(minutes)),
            new CutTotalsDto(remainingKept.Count, remainingKept.Sum(minutes)));
    }
}
