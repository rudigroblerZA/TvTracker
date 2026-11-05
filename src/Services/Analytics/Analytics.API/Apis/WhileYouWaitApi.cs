using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <param name="YourMinutesPerWeek">The caller's pace over the last 8 weeks (the catch-up planner's), or the assumed one.</param>
/// <param name="PaceAssumed">True when the pace is <see cref="WhileYouWait.AssumedMinutesPerWeek"/> rather than measured.</param>
/// <param name="PaceAssumedBecause">Why: <c>nothing-watched</c> (no watches in 8 weeks) or <c>no-runtimes</c> (watches of episodes
/// without running times); null when measured.</param>
/// <param name="Waits">Shows the caller is caught up on with a return in view, soonest first, each with what fits before it.</param>
public record WhileYouWaitDto(int YourMinutesPerWeek, bool PaceAssumed, string? PaceAssumedBecause, IReadOnlyList<WaitDto> Waits);

/// <param name="Kind">As the waiting room: <c>episode</c>, <c>season</c> or <c>forecast</c>.</param>
/// <param name="BackAt">The date it's back in the caller's calendar: the scheduled one, or a forecast's likely date.</param>
/// <param name="BudgetMinutes">Days left × the pace, rounded down: what the caller can get through before then.</param>
/// <param name="Picks">Finished, unstarted shows that fit the budget, the most like this one first.</param>
public record WaitDto(Guid ShowId, string Title, string? PosterUrl, string Kind, DateOnly BackAt, int? SeasonNumber, int DaysLeft,
    int BudgetMinutes, IReadOnlyList<WaitPickDto> Picks);

/// <param name="Value">How much it's likely worth the caller's time (ShowValue), on the rating's scale.</param>
/// <param name="Alike">How alike the two shows are, 0-1 (genres and DNA together).</param>
/// <param name="FinishesAround">When they'd finish it at their pace, starting today.</param>
/// <param name="DaysToSpare">Days between finishing and the return; never negative.</param>
/// <param name="Reasons">Why it fits: shared genres or DNA (or "a change of pace"), then the ending and a slow start.</param>
public record WaitPickDto(Guid ShowId, string Title, string? PosterUrl, int Episodes, int Minutes, double Value, string? Landing,
    double Alike, DateOnly FinishesAround, int DaysToSpare, IReadOnlyList<string> Reasons);

/// <summary>
/// While you wait: for each show the caller is caught up on and whose return is in view, a finished show they can watch
/// from start to end before it's back, chosen for how much it resembles the one they're waiting for. It joins the waiting
/// room (when each show is back, and the copies and documents it loaded), the catch-up planner's pace (how much they
/// watch a week) and Fits your time off's candidates (every finished show worth offering, with its DNA), so it reads
/// nothing new and nothing twice. Computed on request.
/// </summary>
public static class WhileYouWaitApi
{
    public static void MapWhileYouWaitApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/while-you-wait", GetAsync).RequireFeature(FeatureFlags.WhileYouWait);

    /// <param name="tz">The caller's time zone (IANA id, from their profile), so days are counted in their calendar; UTC
    /// when missing or unknown.</param>
    private static async Task<Ok<WhileYouWaitDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, HybridCache cache, TimeProvider time,
        string? tz = null, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var zone = Viewer.Zone(tz);
        var today = Viewer.LocalDate(now, zone);

        var room = await WaitingRoomApi.ComputeAsync(db, userId, now, ct);
        var waiting = room.Shows.Select(w => WhileYouWait.Awaited(w, today, zone)).OfType<WhileYouWait.Wait>().ToList();
        if (waiting.Count == 0)
        {
            return TypedResults.Ok(new WhileYouWaitDto(0, false, null, []));
        }

        var measured = await CatchUpApi.PaceDetailsAsync(db, userId, now, ct);
        var assumedBecause = measured.MinutesPerWeek > 0 ? null : measured.Watches > 0 ? "no-runtimes" : "nothing-watched";
        var pace = assumedBecause is null ? measured.MinutesPerWeek : WhileYouWait.AssumedMinutesPerWeek;
        var candidates = (await TimeOffApi.CandidatesAsync(db, cache, userId, now, ct, room.Started))
            .Where(c => c.Assessment.Value >= WhileYouWait.MinValue)
            .ToList();

        var waits = WhileYouWait.Plan(waiting, candidates, pace, today,
            id => room.Copies.TryGetValue(id, out var copy) ? copy.Genres : [],
            id => room.Documents.GetValueOrDefault(id)?.Dna?.Traits.ToDictionary(t => t.Key, t => t.Value));
        return TypedResults.Ok(new WhileYouWaitDto(pace, assumedBecause is not null, assumedBecause, waits));
    }
}

/// <summary>
/// The rules of While you wait, as pure functions of the waiting room, the candidates and a pace.
/// <list type="bullet">
/// <item><b>A return in view</b> is a dated episode or premiere, or a forecast's likely date, between <see cref="MinDays"/> and
/// <see cref="MaxDays"/> away in the caller's calendar: nearer and there's no time to start anything; further and the forecast
/// is too loose to plan on.</item>
/// <item><b>The budget</b> is the days left at the caller's pace (the catch-up planner's minutes a week; <see cref="AssumedMinutesPerWeek"/>
/// when it can't be measured), rounded down so a pick that fills it still finishes in time. A pick must fit: the point is to
/// finish before the show is back.</item>
/// <item><b>Alike</b> is half the genres' Jaccard overlap and half the DNA similarity rescaled as Recommendations does (0.65 → 0,
/// 0.95 → 1), each half standing alone when the other is unknown. A pick's score is its value plus <see cref="AlikeWeight"/>
/// × alike, so a slightly worse show that's much more like the awaited one wins. A pick under <see cref="MinAlikeToName"/> alike is
/// "a change of pace" and must be worth <see cref="MinValueForChangeOfPace"/> or more.</item>
/// <item><b>Each pick once:</b> waits are planned soonest first, and a show picked for one isn't offered for the next.</item>
/// </list>
/// </summary>
internal static class WhileYouWait
{
    public const int MinDays = 7;
    public const int MaxDays = 180;
    public const int AssumedMinutesPerWeek = 300;
    public const double MinValue = 7.0;
    public const double AlikeWeight = 2.0;
    public const int PicksPerWait = 3;

    /// <summary>
    /// Below this, two shows aren't called alike in the reasons, and the pick is "a change of pace", which has to be at
    /// least <see cref="MinValueForChangeOfPace"/> to be offered at all: a filler nothing like the show should be good.
    /// </summary>
    public const double MinAlikeToName = 0.35;

    public const double MinValueForChangeOfPace = 7.5;

    public sealed record Wait(Guid ShowId, string Title, string? PosterUrl, string Kind, DateOnly BackAt, int? SeasonNumber);

    /// <summary>
    /// A waiting-room entry with a return in view, else null. A dated return is read in the caller's calendar.
    /// </summary>
    public static Wait? Awaited(WaitingShowDto w, DateOnly today, TimeZoneInfo zone)
    {
        DateOnly? back = w.Kind switch
        {
            "episode" or "season" when w.BackAtUtc is { } at => Viewer.LocalDate(at, zone),
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

    public static List<WaitDto> Plan(IReadOnlyList<Wait> waits, IReadOnlyList<TimeOffApi.Candidate> candidates, int pace, DateOnly today,
        Func<Guid, IReadOnlyList<string>> genresOf, Func<Guid, IReadOnlyDictionary<string, double>?> dnaOf)
    {
        var taken = new HashSet<Guid>();
        return waits.OrderBy(w => w.BackAt).Select(wait =>
            {
                var daysLeft = wait.BackAt.DayNumber - today.DayNumber;
                var budget = (int)Math.Floor(daysLeft * pace / 7.0);
                var genres = genresOf(wait.ShowId);
                var dna = dnaOf(wait.ShowId);
                var picks = candidates
                    .Where(c => !taken.Contains(c.Show.ShowId) && c.Show.Minutes <= budget)
                    .Select(c =>
                    {
                        var alike = Alike(genres, dna, c.Show);
                        return (Candidate: c, Alike: alike, Score: c.Assessment.Value + AlikeWeight * alike.Alike,
                            Finishes: today.AddDays((int)Math.Ceiling(c.Show.Minutes * 7.0 / pace)));
                    })
                    .Where(x => (x.Alike.Alike >= MinAlikeToName || x.Candidate.Assessment.Value >= MinValueForChangeOfPace) && x.Finishes <= wait.BackAt)
                    .OrderByDescending(x => x.Score).ThenByDescending(x => x.Candidate.Show.Minutes)
                    .Take(PicksPerWait)
                    .Select(x =>
                    {
                        taken.Add(x.Candidate.Show.ShowId);
                        var assessment = x.Candidate.Assessment;
                        // The rating is shown as the pill; the taste line repeats the genres once Alike has named them.
                        var reasons = x.Alike.Reasons
                            .Concat(assessment.Reasons.Where(r => r != assessment.RatingReason && (r != assessment.TasteReason || !x.Alike.NamedGenres)))
                            .ToList();
                        return new WaitPickDto(x.Candidate.Show.ShowId, x.Candidate.Show.Title, x.Candidate.Show.PosterUrl, x.Candidate.Show.Episodes,
                            x.Candidate.Show.Minutes, assessment.Value, assessment.Landing, Math.Round(x.Alike.Alike, 2), x.Finishes,
                            wait.BackAt.DayNumber - x.Finishes.DayNumber, reasons);
                    })
                    .ToList();
                return new WaitDto(wait.ShowId, wait.Title, wait.PosterUrl, wait.Kind, wait.BackAt, wait.SeasonNumber, daysLeft, budget, picks);
            })
            .ToList();
    }

    /// <param name="NamedGenres">The reasons name shared genres, so a taste line about the same genres would repeat them.</param>
    internal sealed record Likeness(double Alike, List<string> Reasons, bool NamedGenres);

    /// <summary>
    /// How alike a candidate is to the awaited show, 0-1, with the reasons that say so.
    /// </summary>
    internal static Likeness Alike(IReadOnlyList<string> genres, IReadOnlyDictionary<string, double>? dna, TimeOffApi.ShelfShow candidate)
    {
        var reasons = new List<string>();
        var parts = new List<double>();
        var namedGenres = false;

        if (genres.Count > 0 && candidate.Genres.Count > 0)
        {
            var shared = genres.Intersect(candidate.Genres).ToList();
            parts.Add((double)shared.Count / genres.Union(candidate.Genres).Count());
            if (shared.Count > 0)
            {
                reasons.Add($"Also {string.Join(", ", shared.Select(g => g.ToLowerInvariant()))}");
                namedGenres = true;
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
            return new Likeness(alike, ["A change of pace"], false);
        }
        return new Likeness(alike, reasons, namedGenres);
    }
}
