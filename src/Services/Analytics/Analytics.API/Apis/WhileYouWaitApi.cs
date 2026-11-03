using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <param name="YourMinutesPerWeek">The caller's pace over the last 8 weeks (the catch-up planner's), or the assumed one.</param>
/// <param name="PaceAssumed">True when they've watched nothing lately, so the pace is <see cref="WhileYouWait.AssumedMinutesPerWeek"/>.</param>
/// <param name="Waits">Shows the caller is caught up on with a return in view, soonest first, each with what fits before it.</param>
public record WhileYouWaitDto(int YourMinutesPerWeek, bool PaceAssumed, IReadOnlyList<WaitDto> Waits);

/// <param name="Kind">As the waiting room: <c>episode</c>, <c>season</c> or <c>forecast</c>.</param>
/// <param name="BackAt">The date it's back: the scheduled one, or a forecast's likely date.</param>
/// <param name="BudgetMinutes">Days left × the pace: what the caller can get through before then.</param>
/// <param name="Picks">Finished, unstarted shows that fit the budget, the most like this one first.</param>
public record WaitDto(Guid ShowId, string Title, string? PosterUrl, string Kind, DateOnly BackAt, int? SeasonNumber, int DaysLeft,
    int BudgetMinutes, IReadOnlyList<WaitPickDto> Picks);

/// <param name="Value">How much it's likely worth the caller's time (ShowValue), on the rating's scale.</param>
/// <param name="Alike">How alike the two shows are, 0-1 (genres and DNA together).</param>
/// <param name="FinishesAround">When they'd finish it at their pace, starting now.</param>
/// <param name="DaysToSpare">Days between finishing and the return.</param>
/// <param name="Reasons">Why it fits: shared genres, shared DNA, the ending.</param>
public record WaitPickDto(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes, double Value, string? Landing,
    double Alike, DateOnly FinishesAround, int DaysToSpare, IReadOnlyList<string> Reasons);

/// <summary>
/// While you wait: for each show the caller is caught up on and whose return is in view, a finished show they can watch
/// from start to end before it's back, chosen for how much it resembles the one they're waiting for. It joins the waiting
/// room (when each show is back), the catch-up planner's pace (how much they watch a week) and Fits your time off's shelf
/// (every finished show with its running time, ending and DNA), so it reads nothing new. Computed on request.
/// </summary>
public static class WhileYouWaitApi
{
    public static void MapWhileYouWaitApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/while-you-wait", GetAsync).RequireFeature(FeatureFlags.WhileYouWait);

    private static async Task<Ok<WhileYouWaitDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, HybridCache cache, TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var waiting = (await WaitingRoomApi.ComputeAsync(db, userId, now, ct))
            .Select(w => WhileYouWait.Awaited(w, today))
            .OfType<WhileYouWait.Wait>()
            .ToList();
        if (waiting.Count == 0)
        {
            return TypedResults.Ok(new WhileYouWaitDto(0, false, []));
        }

        var measured = await CatchUpApi.PaceAsync(db, userId, now, ct);
        var pace = measured > 0 ? measured : WhileYouWait.AssumedMinutesPerWeek;
        var shelf = await TimeOffApi.GetShelfAsync(db, cache, now, ct);
        var started = (await db.Watches.AsNoTracking().Where(w => w.UserId == userId).Select(w => w.ShowId).Distinct().ToListAsync(ct)).ToHashSet();
        var liked = await ShowValue.LikedGenresAsync(db, userId, now, ct);

        // The shows being waited for: their genres from the copy, their DNA from the documents.
        var awaitedIds = waiting.Select(w => w.ShowId).ToList();
        var genres = await db.Shows.AsNoTracking().Where(s => awaitedIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, s => (IReadOnlyList<string>)s.Genres, ct);
        var documents = await ShowDocuments.LoadAsync(db, awaitedIds, ct);

        var candidates = shelf.Shows
            .Where(s => !started.Contains(s.ShowId) && s.Minutes >= WhileYouWait.MinMinutes)
            .Select(s => new WhileYouWait.Candidate(s, ShowValue.Assess(s.ExternalRating, s.Genres, s.Landing, s.Turn, s.Limited, liked)))
            .Where(c => c.Assessment.Value >= WhileYouWait.MinValue && c.Assessment.Landing is not LandingVerdicts.Fizzles)
            .ToList();

        var waits = WhileYouWait.Plan(waiting, candidates, pace, today,
            id => genres.GetValueOrDefault(id, []),
            id => documents.GetValueOrDefault(id)?.Dna?.Traits.ToDictionary(t => t.Key, t => t.Value));
        return TypedResults.Ok(new WhileYouWaitDto(pace, measured <= 0, waits));
    }
}

/// <summary>
/// The rules of While you wait, as pure functions of the waiting room, the shelf and a pace.
/// <list type="bullet">
/// <item><b>A return in view</b> is a dated episode or premiere, or a forecast's likely date, between <see cref="MinDays"/> and
/// <see cref="MaxDays"/> away: nearer and there's no time to start anything; further and the forecast is too loose to plan on.</item>
/// <item><b>The budget</b> is the days left at the caller's pace (the catch-up planner's minutes a week; <see cref="AssumedMinutesPerWeek"/>
/// when they've watched nothing lately), and a pick must fit it: the point is to finish before the show is back.</item>
/// <item><b>Alike</b> is half the genres' Jaccard overlap and half the DNA similarity rescaled as Recommendations does (0.65 → 0,
/// 0.95 → 1), each half standing alone when the other is unknown. A pick's score is its value plus <see cref="AlikeWeight"/>
/// × alike, so a slightly worse show that's much more like the awaited one wins.</item>
/// <item><b>Each pick once:</b> waits are planned soonest first, and a show picked for one isn't offered for the next.</item>
/// </list>
/// </summary>
internal static class WhileYouWait
{
    public const int MinDays = 7;
    public const int MaxDays = 180;
    public const int AssumedMinutesPerWeek = 300;
    public const int MinMinutes = 60;
    public const double MinValue = 7.0;
    public const double AlikeWeight = 2.0;
    public const int PicksPerWait = 3;

    /// <summary>
    /// Below this, two shows aren't called alike in the reasons.
    /// </summary>
    public const double MinAlikeToName = 0.35;

    public sealed record Wait(Guid ShowId, string Title, string? PosterUrl, string Kind, DateOnly BackAt, int? SeasonNumber);

    public sealed record Candidate(TimeOffApi.ShelfShow Show, ShowValue.Assessment Assessment);

    /// <summary>
    /// A waiting-room entry with a return in view, else null.
    /// </summary>
    public static Wait? Awaited(WaitingShowDto w, DateOnly today)
    {
        DateOnly? back = w.Kind switch
        {
            "episode" or "season" when w.BackAtUtc is { } at => DateOnly.FromDateTime(at.UtcDateTime),
            "forecast" when !w.Overdue => w.Likely,
            _ => null
        };
        if (back is not { } date)
        {
            return null;
        }
        var days = date.DayNumber - today.DayNumber;
        return days is >= MinDays and <= MaxDays ? new Wait(w.ShowId, w.Title, w.PosterUrl, w.Kind, date, w.SeasonNumber) : null;
    }

    public static List<WaitDto> Plan(IReadOnlyList<Wait> waits, IReadOnlyList<Candidate> candidates, int pace, DateOnly today,
        Func<Guid, IReadOnlyList<string>> genresOf, Func<Guid, IReadOnlyDictionary<string, double>?> dnaOf)
    {
        var taken = new HashSet<Guid>();
        return waits.OrderBy(w => w.BackAt).Select(wait =>
            {
                var daysLeft = wait.BackAt.DayNumber - today.DayNumber;
                var budget = (int)Math.Round(daysLeft * pace / 7.0);
                var genres = genresOf(wait.ShowId);
                var dna = dnaOf(wait.ShowId);
                var picks = candidates
                    .Where(c => !taken.Contains(c.Show.ShowId) && c.Show.Minutes <= budget)
                    .Select(c =>
                    {
                        var (alike, reasons) = Alike(genres, dna, c.Show);
                        return (Candidate: c, Alike: alike, Reasons: reasons, Score: c.Assessment.Value + AlikeWeight * alike);
                    })
                    .OrderByDescending(x => x.Score).ThenByDescending(x => x.Candidate.Show.Minutes)
                    .Take(PicksPerWait)
                    .Select(x =>
                    {
                        taken.Add(x.Candidate.Show.ShowId);
                        var finishes = today.AddDays((int)Math.Ceiling(x.Candidate.Show.Minutes * 7.0 / pace));
                        var reasons = x.Reasons.Concat(x.Candidate.Assessment.Reasons.Where(r => !r.StartsWith("Rated", StringComparison.Ordinal))).ToList();
                        return new WaitPickDto(x.Candidate.Show.ShowId, x.Candidate.Show.Title, x.Candidate.Show.PosterUrl, x.Candidate.Show.Episodes,
                            x.Candidate.Show.Minutes, x.Candidate.Assessment.Value, x.Candidate.Assessment.Landing, Math.Round(x.Alike, 2), finishes,
                            wait.BackAt.DayNumber - finishes.DayNumber, reasons);
                    })
                    .ToList();
                return new WaitDto(wait.ShowId, wait.Title, wait.PosterUrl, wait.Kind, wait.BackAt, wait.SeasonNumber, daysLeft, budget, picks);
            })
            .ToList();
    }

    /// <summary>
    /// How alike a candidate is to the awaited show, 0-1, with the reasons that say so.
    /// </summary>
    internal static (double Alike, List<string> Reasons) Alike(IReadOnlyList<string> genres, IReadOnlyDictionary<string, double>? dna,
        TimeOffApi.ShelfShow candidate)
    {
        var reasons = new List<string>();
        var parts = new List<double>();

        if (genres.Count > 0 && candidate.Genres.Count > 0)
        {
            var shared = genres.Intersect(candidate.Genres).ToList();
            parts.Add((double)shared.Count / genres.Union(candidate.Genres).Count());
            if (shared.Count > 0)
            {
                reasons.Add($"Also {string.Join(", ", shared.Select(g => g.ToLowerInvariant()))}");
            }
        }

        if (dna is not null && candidate.Dna is not null && DnaTraits.Similarity(dna, candidate.Dna) is { } similarity)
        {
            // Similarities cluster between 0.6 and 0.95 (Recommendations' affinity), so the band is stretched to 0-1.
            parts.Add(Math.Clamp((similarity - 0.65) / 0.3, 0, 1));
            var traits = DnaTraits.All
                .Where(t => dna.TryGetValue(t.Key, out var a) && candidate.Dna.TryGetValue(t.Key, out var b)
                    && DnaTraits.Distinctiveness(t.Key, a) > 0 && DnaTraits.LabelFor(t.Key, a) == DnaTraits.LabelFor(t.Key, b))
                .Select(t => DnaTraits.LabelFor(t.Key, dna[t.Key])!.ToLowerInvariant())
                .Take(2)
                .ToList();
            if (traits.Count > 0 && similarity >= 0.75)
            {
                reasons.Add($"Made alike: {string.Join(", ", traits)}");
            }
        }

        var alike = parts.Count == 0 ? 0 : parts.Average();
        if (alike < MinAlikeToName)
        {
            reasons.Clear();
            reasons.Add("A change of pace");
        }
        return (alike, reasons);
    }
}
