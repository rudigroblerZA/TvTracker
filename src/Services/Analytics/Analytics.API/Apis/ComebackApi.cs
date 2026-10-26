using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="StoppedAt">The furthest episode watched (seen, so its title is safe).</param>
/// <param name="Verdict">
/// "gets-good" (a slow start's turn is still ahead), "better" (a later stretch rates clearly higher than where they
/// stopped), "same", "worse" (what's left rates clearly lower), or "unknown" (too few ratings either side).
/// </param>
/// <param name="BeforeYouStopped">The average rating of the last <see cref="ComebackApi.Window"/> rated episodes watched.</param>
/// <param name="Ahead">The average rating of everything after them.</param>
/// <param name="BetterFrom">Where it picks up, for "better".</param>
/// <param name="GetsGoodAt">The turn, for "gets-good"; no title, since it's ahead of the user.</param>
/// <param name="YourAverage">The user's own ratings (stars × 2) of what they watched, with three or more.</param>
public record ComebackDto(
    Guid ShowId,
    string Title,
    string? PosterUrl,
    int Watched,
    int Aired,
    DateTime LastWatchedAtUtc,
    ComebackEpisodeDto StoppedAt,
    string Verdict,
    double? BeforeYouStopped,
    double? Ahead,
    int AheadEpisodes,
    int AheadMinutes,
    ComebackFromDto? BetterFrom,
    ComebackTurnDto? GetsGoodAt,
    double? YourAverage);

public record ComebackEpisodeDto(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title);

/// <param name="RestOfSeason">It picks up in the rest of the season they stopped in, rather than a later one.</param>
public record ComebackFromDto(int SeasonNumber, bool RestOfSeason, double Average);

/// <param name="EpisodesAway">Episodes between where they stopped and the turn.</param>
public record ComebackTurnDto(int SeasonNumber, int EpisodeNumber, int EpisodesAway, int MinutesAway);

/// <summary>
/// "Is it worth going back?" for a show the user dropped (episodes left, nothing watched for
/// <see cref="InsightRules.StoppedAfterDays"/> days). What's left is compared with the stretch they stopped in, not the
/// whole show: that stretch is likely why they stopped. The essential cut's ratings (the provider's, averaged with our
/// users' once enough rate an episode) are used. Computed on request from the caller's own watches.
/// </summary>
public static class ComebackApi
{
    /// <summary>The stretch before stopping: this many rated episodes.</summary>
    public const int Window = 10;

    /// <summary>A part needs this many rated episodes to be compared.</summary>
    private const int MinRated = 3;

    /// <summary>A difference this big (0-10 scale) is better or worse; less is more of the same.</summary>
    private const double Margin = 0.3;

    /// <summary>A difference of at least <see cref="Margin"/>, allowing for floating point (8.1 − 7.8 is 0.2999…).</summary>
    private static bool Reaches(double difference) => difference >= Margin - 1e-9;

    public static void MapComebackApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/{showId:guid}/comeback", GetOneAsync).RequireFeature(FeatureFlags.WorthGoingBack);

    /// <summary>204 unless the user dropped the show.</summary>
    private static async Task<Results<Ok<ComebackDto>, NoContent, UnauthorizedHttpResult>> GetOneAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        return await BuildAsync(db, userId, showId, time.GetUtcNow(), ct) is { } comeback ? TypedResults.Ok(comeback) : TypedResults.NoContent();
    }

    /// <summary>Every show the user dropped, most recently dropped first.</summary>
    internal static async Task<List<ComebackDto>> AllAsync(AnalyticsContext db, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.UtcDateTime.AddDays(-InsightRules.StoppedAfterDays);
        var paused = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .GroupBy(w => w.ShowId)
            .Select(g => new { ShowId = g.Key, Last = g.Max(w => w.WatchedAtUtc) })
            .Where(g => g.Last < cutoff)
            .OrderByDescending(g => g.Last)
            .ToListAsync(ct);

        var dropped = new List<ComebackDto>();
        foreach (var show in paused)
        {
            if (await BuildAsync(db, userId, show.ShowId, now, ct) is { } comeback)
            {
                dropped.Add(comeback);
            }
        }
        return dropped;
    }

    private static async Task<ComebackDto?> BuildAsync(AnalyticsContext db, string userId, Guid showId, DateTimeOffset now, CancellationToken ct)
    {
        var watches = await db.Watches.AsNoTracking().Where(w => w.UserId == userId && w.ShowId == showId).ToListAsync(ct);
        if (watches.Count == 0 || watches.Max(w => w.WatchedAtUtc) >= now.UtcDateTime.AddDays(-InsightRules.StoppedAfterDays))
        {
            return null;
        }

        var show = await db.Shows.AsNoTracking().SingleOrDefaultAsync(s => s.ShowId == showId, ct);
        if (show is null)
        {
            return null;
        }

        var aired = await db.Episodes.AsNoTracking()
            .Where(e => e.ShowId == showId && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .ToListAsync(ct);
        var watched = watches.Select(w => w.EpisodeId).ToHashSet();
        var furthest = aired.FindLastIndex(e => watched.Contains(e.EpisodeId));
        var ahead = aired.Skip(furthest + 1).ToList();
        if (furthest < 0 || ahead.Count == 0)
        {
            return null;
        }

        var rating = await EssentialCutApi.RatingsAsync(db, showId, ct);
        var minutes = EssentialCutApi.MinutesOf(aired);
        var stoppedAt = aired[furthest];
        var before = aired.Take(furthest + 1).Select(rating).OfType<double>().TakeLast(Window).ToList();
        var after = ahead.Select(rating).OfType<double>().ToList();
        double? baseline = before.Count >= MinRated ? Math.Round(before.Average(), 1) : null;
        double? aheadAverage = after.Count >= MinRated ? Math.Round(after.Average(), 1) : null;

        var stars = await db.EpisodeRatings.AsNoTracking()
            .Where(r => r.UserId == userId && r.ShowId == showId)
            .Select(r => r.Stars * 2.0)
            .ToListAsync(ct);
        double? yours = stars.Count >= MinRated ? Math.Round(stars.Average(), 1) : null;

        // A slow start's turn still ahead is the strongest signal: it's checked against what viewers say.
        var insights = (await ShowValue.InsightsAsync(db, [showId], ct)).GetValueOrDefault(showId);
        ComebackTurnDto? turnAhead = null;
        if (insights?.GetsGood?.Turn is { } turn && aired.FindIndex(e => e.EpisodeId == turn.EpisodeId) is var turnIndex && turnIndex > furthest)
        {
            var between = aired.Skip(furthest + 1).Take(turnIndex - furthest - 1).ToList();
            turnAhead = new ComebackTurnDto(turn.SeasonNumber, turn.EpisodeNumber, between.Count, between.Sum(minutes));
        }

        ComebackFromDto? betterFrom = null;
        if (baseline is { } bar)
        {
            foreach (var season in ahead.GroupBy(e => e.SeasonNumber))
            {
                var ratings = season.Select(rating).OfType<double>().ToList();
                if (ratings.Count >= MinRated && Reaches(ratings.Average() - bar))
                {
                    betterFrom = new ComebackFromDto(season.Key, season.Key == stoppedAt.SeasonNumber, Math.Round(ratings.Average(), 1));
                    break;
                }
            }
        }

        var verdict = turnAhead is not null ? "gets-good"
            : baseline is null || aheadAverage is null ? "unknown"
            : betterFrom is not null ? "better"
            : Reaches(baseline.Value - aheadAverage.Value) ? "worse"
            : "same";

        return new ComebackDto(
            showId, show.Title, show.PosterUrl,
            aired.Count(e => watched.Contains(e.EpisodeId)), aired.Count,
            watches.Max(w => w.WatchedAtUtc),
            new ComebackEpisodeDto(stoppedAt.EpisodeId, stoppedAt.SeasonNumber, stoppedAt.EpisodeNumber, stoppedAt.Title),
            verdict, baseline, aheadAverage, ahead.Count, ahead.Sum(minutes),
            verdict == "better" ? betterFrom : null,
            turnAhead,
            yours);
    }
}
