using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <param name="Fits">Finished shows that fit the time on their own, best first.</param>
/// <param name="Mix">Two or three of them that fill the time better than any one, when there are.</param>
public record TimeOffDto(int BudgetMinutes, IReadOnlyList<TimeOffShowDto> Fits, TimeOffMixDto? Mix);

/// <param name="Value">How much it's likely worth the user's time (see ShowValue), on the rating's 0-10 scale.</param>
/// <param name="Landing">How it ends, when known (a <see cref="LandingVerdicts"/> verdict).</param>
public record TimeOffShowDto(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes, double Value, string? Landing,
    bool Limited, IReadOnlyList<string> Reasons);

public record TimeOffMixDto(IReadOnlyList<TimeOffShowDto> Shows, int Minutes);

/// <summary>
/// "Fits your time off": finished shows the user can watch from start to end in the time they have (a holiday, a long
/// weekend), ranked by how much they're likely worth it to them. Only ended shows: the point is an ending. Shows the user
/// has started are left out, as are shows that fizzle out at the end. Computed on request.
/// </summary>
public static class TimeOffApi
{
    private const int MinHours = 2;
    private const int MaxHours = 200;

    /// <summary>A single fit fills at least this share of the time, so a 20-hour holiday isn't offered a 2-hour show.</summary>
    private const double MinFill = 0.25;

    /// <summary>Candidates for a mix, best first.</summary>
    private const int MixCandidates = 30;

    /// <summary>A mix replaces nothing unless it fills this share of the time.</summary>
    private const double MinMixFill = 0.6;

    private const int MaxFits = 10;

    /// <summary>Shows worth less than this aren't suggested at all.</summary>
    private const double MinValue = 7.3;

    /// <summary>Shorter than this isn't a show to fill time off with.</summary>
    private const int MinMinutes = 60;

    /// <summary>A mix's score is Σ minutes × (value − this): a show at this value adds nothing by being longer.</summary>
    private const double MixValueFloor = 6.5;

    /// <summary>The finished shows and what's known about their endings are the same for everyone; kept this long.</summary>
    private static readonly HybridCacheEntryOptions ShelfCache = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) };

    /// <summary>A finished show on the shelf, with the parts of its document the assessment reads.</summary>
    public sealed record ShelfShow(ShowCopy Show, int Episodes, int Minutes, string? Landing, GetsGoodTurn? Turn, bool Limited);

    public static void MapTimeOffApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/time-off", GetAsync).RequireFeature(FeatureFlags.TimeOff);

    private static async Task<Results<Ok<TimeOffDto>, ValidationProblem>> GetAsync(
        ClaimsPrincipal user, AnalyticsContext db, HybridCache cache, TimeProvider time, int hours = 20, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        if (hours is < MinHours or > MaxHours)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = [$"Use {MinHours} to {MaxHours} hours."] });
        }

        var now = time.GetUtcNow();
        var budget = hours * 60;
        var overrun = Viewer.Overrun(budget);
        // The shelf (every finished show, its running time and its ending) is read once an hour, not per request: it
        // used to mean loading and deserializing several hundred documents for each visit.
        var shelf = await cache.GetOrCreateAsync("time-off:shelf", async token => await ShelfAsync(db, now, token), ShelfCache, cancellationToken: ct);
        var started = (await db.Watches.AsNoTracking().Where(w => w.UserId == userId).Select(w => w.ShowId).Distinct().ToListAsync(ct)).ToHashSet();
        var liked = await ShowValue.LikedGenresAsync(db, userId, now, ct);

        var candidates = shelf
            .Where(s => !started.Contains(s.Show.ShowId) && s.Minutes >= MinMinutes && s.Minutes <= budget + overrun)
            .Select(s =>
            {
                var assessment = ShowValue.Assess(s.Show, s.Landing, s.Turn, s.Limited, liked);
                return new TimeOffShowDto(s.Show.ShowId, s.Show.Title, s.Show.PosterUrl, s.Episodes, s.Minutes, assessment.Value,
                    assessment.Landing, assessment.Limited, assessment.Reasons);
            })
            .Where(c => c.Value >= MinValue && c.Landing is not LandingVerdicts.Fizzles)
            .OrderByDescending(c => c.Value)
            .ThenByDescending(c => c.Minutes)
            .ToList();

        var fits = candidates.Where(c => c.Minutes >= budget * MinFill).Take(MaxFits).ToList();
        return TypedResults.Ok(new TimeOffDto(budget, fits, Mix(candidates.Take(MixCandidates).ToList(), budget + overrun, fits)));
    }

    /// <summary>Every ended show with a running time, and the parts of its document the assessment reads.</summary>
    private static async Task<List<ShelfShow>> ShelfAsync(AnalyticsContext db, DateTimeOffset now, CancellationToken ct)
    {
        var ended = await db.Shows.AsNoTracking().Where(s => s.Status == ShowStatus.Ended || s.Status == ShowStatus.Cancelled).ToListAsync(ct);
        var runs = await ShowValue.RunTimesAsync(db, ended.Select(s => s.ShowId).ToList(), now, ct);
        var insights = await ShowDocuments.LoadAsync(db, runs.Keys.ToList(), ct);
        return ended.Where(s => runs.ContainsKey(s.ShowId))
            .Select(s =>
            {
                var document = insights.GetValueOrDefault(s.ShowId);
                return new ShelfShow(s, runs[s.ShowId].Episodes, runs[s.ShowId].Minutes, document?.Landing?.Verdict, document?.GetsGood?.Turn,
                    document?.Tags?.Any(t => t.Key == ShowTags.LimitedSeries) == true);
            })
            .ToList();
    }

    /// <summary>
    /// The best two or three shows together, by minutes × (value − <see cref="MixValueFloor"/>): a fuller evening of good shows beats a short one
    /// of great shows, as in Tonight's plan. Only offered when it fills more of the time than the best single fit.
    /// </summary>
    private static TimeOffMixDto? Mix(List<TimeOffShowDto> candidates, int limit, List<TimeOffShowDto> fits)
    {
        double Score(IEnumerable<TimeOffShowDto> shows) => shows.Sum(s => s.Minutes * (s.Value - MixValueFloor));

        List<TimeOffShowDto>? best = null;
        void Consider(List<TimeOffShowDto> mix)
        {
            if (mix.Sum(s => s.Minutes) <= limit && (best is null || Score(mix) > Score(best)))
            {
                best = mix;
            }
        }

        for (var a = 0; a < candidates.Count; a++)
        {
            for (var b = a + 1; b < candidates.Count; b++)
            {
                Consider([candidates[a], candidates[b]]);
                for (var c = b + 1; c < candidates.Count; c++)
                {
                    Consider([candidates[a], candidates[b], candidates[c]]);
                }
            }
        }

        var minutes = best?.Sum(s => s.Minutes) ?? 0;
        var bestSingle = fits.Select(f => f.Minutes).DefaultIfEmpty(0).Max();
        return best is not null && minutes >= limit * MinMixFill && minutes > bestSingle
            ? new TimeOffMixDto(best.OrderByDescending(s => s.Value).ToList(), minutes)
            : null;
    }
}
