using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
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

    public static void MapTimeOffApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/time-off", GetAsync).RequireFeature(FeatureFlags.TimeOff);

    private static async Task<Results<Ok<TimeOffDto>, UnauthorizedHttpResult, ValidationProblem>> GetAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, int hours = 20, CancellationToken ct = default)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        if (hours is < MinHours or > MaxHours)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = [$"Use {MinHours} to {MaxHours} hours."] });
        }

        var now = time.GetUtcNow();
        var budget = hours * 60;
        var overrun = Math.Max(5, budget / 12);
        var started = db.Watches.Where(w => w.UserId == userId).Select(w => w.ShowId);
        var ended = await db.Shows.AsNoTracking()
            .Where(s => s.Status == ShowStatus.Ended && !started.Contains(s.ShowId))
            .ToListAsync(ct);
        var runs = await ShowValue.RunTimesAsync(db, ended.Select(s => s.ShowId).ToList(), now, ct);
        var fitting = ended
            .Where(s => runs.TryGetValue(s.ShowId, out var run) && run.Minutes >= 60 && run.Minutes <= budget + overrun)
            .ToList();
        var insights = await ShowValue.InsightsAsync(db, fitting.Select(s => s.ShowId).ToList(), ct);
        var liked = await ShowValue.LikedGenresAsync(db, userId, now, ct);

        var candidates = fitting
            .Select(s =>
            {
                var assessment = ShowValue.Assess(s, insights.GetValueOrDefault(s.ShowId), liked);
                return new TimeOffShowDto(s.ShowId, s.Title, s.PosterUrl, runs[s.ShowId].Episodes, runs[s.ShowId].Minutes, assessment.Value,
                    assessment.Landing, assessment.Limited, assessment.Reasons);
            })
            .Where(c => c.Value >= MinValue && c.Landing is not LandingVerdicts.Fizzles)
            .OrderByDescending(c => c.Value)
            .ThenByDescending(c => c.Minutes)
            .ToList();

        var fits = candidates.Where(c => c.Minutes >= budget * MinFill).Take(MaxFits).ToList();
        return TypedResults.Ok(new TimeOffDto(budget, fits, Mix(candidates.Take(MixCandidates).ToList(), budget + overrun, fits)));
    }

    /// <summary>
    /// The best two or three shows together, by minutes × (value − 6.5): a fuller evening of good shows beats a short one
    /// of great shows, as in Tonight's plan. Only offered when it fills more of the time than the best single fit.
    /// </summary>
    private static TimeOffMixDto? Mix(List<TimeOffShowDto> candidates, int limit, List<TimeOffShowDto> fits)
    {
        double Score(IEnumerable<TimeOffShowDto> shows) => shows.Sum(s => s.Minutes * (s.Value - 6.5));

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
