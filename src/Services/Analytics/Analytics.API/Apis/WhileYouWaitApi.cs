using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Analytics.API.Apis;

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
        var zone = ViewerRules.Zone(tz);
        var today = ViewerRules.LocalDate(now, zone);

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
