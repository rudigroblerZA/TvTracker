using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="Minutes">Everything on the list: shows not started, and what's left of dropped ones.</param>
/// <param name="YourMinutesPerWeek">The user's pace, as the catch-up planner measures it.</param>
/// <param name="WeeksToClear">At that pace; null without one.</param>
/// <param name="LetGoMinutes">What letting go of the "let-it-go" shows, and dropped shows that only get worse, saves.</param>
/// <param name="CutSavesMinutes">What watching the long shows in their essential cut saves.</param>
/// <param name="Dropped">Shows the user dropped, with whether they're worth going back to.</param>
public record BacklogDto(
    int Minutes,
    int YourMinutesPerWeek,
    int? WeeksToClear,
    int LetGoMinutes,
    int CutSavesMinutes,
    IReadOnlyList<BacklogShowDto> Shows,
    IReadOnlyList<ComebackDto> Dropped);

/// <param name="Value">How much it's likely worth the user's time, on the rating's 0-10 scale (see ShowValue).</param>
/// <param name="Verdict">"start-next", "worth-it", "long" (<see cref="BacklogApi.LongMinutes"/> or more; the essential cut
/// is offered), "ends-badly", or "let-it-go".</param>
/// <param name="CutMinutes">The essential cut's length, for long shows that have one.</param>
public record BacklogShowDto(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes, int? CutMinutes, double Value,
    string Verdict, IReadOnlyList<string> Reasons);

/// <summary>
/// Backlog triage: the followed shows the user hasn't started, ranked by how much they're likely worth the user's time,
/// with a plain verdict each, plus the shows they dropped and whether to go back. Analytics doesn't know Tracking's
/// "plan to watch" status, so the backlog is followed shows with nothing watched, which is what that status mostly means.
/// Computed on request from the caller's own data.
/// </summary>
public static class BacklogApi
{
    /// <summary>Shows this long or longer are "long": worth it, but the essential cut is offered.</summary>
    public const int LongMinutes = 40 * 60;

    /// <summary>Below this value, a show is "let it go".</summary>
    private const double LetGoBelow = 7.0;

    /// <summary>A show that ends badly is still worth starting from this value.</summary>
    private const double EndsBadlyBelow = 8.0;

    private const int StartNext = 3;

    public static void MapBacklogApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/backlog", GetAsync).RequireFeature(FeatureFlags.BacklogTriage);

    private static async Task<Ok<BacklogDto>> GetAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var started = db.Watches.Where(w => w.UserId == userId).Select(w => w.ShowId);
        var notStarted = await db.Follows.AsNoTracking()
            .Where(f => f.UserId == userId && !started.Contains(f.ShowId))
            .Select(f => f.ShowId)
            .ToListAsync(ct);
        var runs = await ShowValue.RunTimesAsync(db, notStarted, now, ct);
        var shows = await db.Shows.AsNoTracking().Where(s => runs.Keys.Contains(s.ShowId)).ToListAsync(ct);
        var insights = await ShowValue.InsightsAsync(db, runs.Keys.ToList(), ct);
        var liked = await ShowValue.LikedGenresAsync(db, userId, now, ct);

        var assessed = shows.Where(s => runs[s.ShowId].Episodes > 0)
            .Select(show =>
            {
                var assessment = ShowValue.Assess(show, insights.GetValueOrDefault(show.ShowId), liked);
                var verdict = assessment.Value < LetGoBelow ? "let-it-go"
                    : assessment.Landing is LandingVerdicts.Fizzles or LandingVerdicts.FinaleLetsDown && assessment.Value < EndsBadlyBelow ? "ends-badly"
                    : runs[show.ShowId].Minutes >= LongMinutes ? "long"
                    : "worth-it";
                return (Show: show, Assessment: assessment, Verdict: verdict);
            })
            .ToList();
        // The lean cut's running time for the long shows, from one load of their episodes, ratings and notes.
        var cuts = await ShowLoads.LoadAsync(db, userId, assessed.Where(a => a.Verdict == "long").Select(a => a.Show.ShowId).ToList(), now,
            withInsights: false, ct);
        var items = assessed
            .Select(a => new BacklogShowDto(a.Show.ShowId, a.Show.Title, a.Show.PosterUrl, runs[a.Show.ShowId].Episodes, runs[a.Show.ShowId].Minutes,
                cuts.TryGetValue(a.Show.ShowId, out var load) ? CutMinutes(load, now) : null, a.Assessment.Value, a.Verdict, a.Assessment.Reasons))
            .ToList();

        // The best few that aren't long go first.
        var next = items.Where(i => i.Verdict == "worth-it").OrderByDescending(i => i.Value).Take(StartNext).Select(i => i.ShowId).ToHashSet();
        items = items
            .Select(i => next.Contains(i.ShowId) ? i with { Verdict = "start-next" } : i)
            .OrderBy(i => Array.IndexOf(Order, i.Verdict))
            .ThenByDescending(i => i.Value)
            .ToList();

        var dropped = await ComebackApi.AllAsync(db, userId, now, ct);
        var total = items.Sum(i => i.Minutes) + dropped.Sum(d => d.AheadMinutes);
        var letGo = items.Where(i => i.Verdict == "let-it-go").Sum(i => i.Minutes) + dropped.Where(d => d.Verdict == "worse").Sum(d => d.AheadMinutes);
        var cutSaves = items.Where(i => i.CutMinutes is not null).Sum(i => i.Minutes - i.CutMinutes!.Value);
        var pace = await CatchUpApi.PaceAsync(db, userId, now, ct);

        return TypedResults.Ok(new BacklogDto(total, pace, pace > 0 ? (int)Math.Ceiling((double)total / pace) : null, letGo, cutSaves,
            items, dropped));
    }

    private static readonly string[] Order = ["start-next", "worth-it", "long", "ends-badly", "let-it-go"];

    /// <summary>The "lean" essential cut's running time; null when the show can't be cut or it saves nothing.</summary>
    private static int? CutMinutes(ShowLoad load, DateTimeOffset now)
    {
        var aired = load.Aired;
        if (EssentialCutApi.Unavailable(aired, load.Rating, load.Notes) is not null)
        {
            return null;
        }

        var decisions = EssentialCutApi.Decide(load.Show, load.Episodes, aired, load.Rating, EssentialCutApi.Lean, now, load.Notes);
        var kept = aired.Where(e => decisions[e.EpisodeId].Keep).Sum(load.Minutes);
        return kept < aired.Sum(load.Minutes) ? kept : null;
    }
}
