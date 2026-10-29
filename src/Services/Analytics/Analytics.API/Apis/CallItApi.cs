using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <summary>A show's "Call it" panel: its next season to call, and the caller's calls on it.</summary>
/// <param name="SeasonNumber">The season open to a call; null for an ended show (only past calls then).</param>
/// <param name="PremieresAtUtc">Its first dated episode, when one is listed.</param>
/// <param name="Mine">The caller's call on that season.</param>
/// <param name="Model">The forecast model's rating for it, with its 80% range; only once the caller has called, so it
/// doesn't anchor them.</param>
/// <param name="Crowd">Everyone's average call, with how many; only once the caller has called, and with 5+ callers.</param>
/// <param name="Past">The caller's earlier calls on this show, latest first.</param>
public record ShowCallDto(int? SeasonNumber, DateTimeOffset? PremieresAtUtc, double? Mine, CallRangeDto? Model, CrowdCallDto? Crowd,
    IReadOnlyList<CallDto> Past);

public record CallRangeDto(double Rating, double Low, double High);

public record CrowdCallDto(double Rating, int Callers);

/// <param name="State">"open" (before its premiere), "airing" (premiered, not over yet) or "scored".</param>
/// <param name="Model">The model's rating when the call was made; null when it had none.</param>
/// <param name="Actual">The season's average episode rating, once scored.</param>
/// <param name="BeatModel">Scored with a model: true when the caller was closer, false when the model was; null on a tie.</param>
public record CallDto(Guid ShowId, string Title, string? PosterUrl, int SeasonNumber, double Rating, double? Model, double? Actual,
    string State, bool? BeatModel, DateTimeOffset? PremieresAtUtc);

/// <param name="Scored">Calls scored so far; <paramref name="AverageMiss"/> is the caller's mean distance from the
/// actual rating, and <paramref name="ModelAverageMiss"/> the model's on the same calls (those it had a forecast for).</param>
public record CallRecordDto(int Scored, double? AverageMiss, double? ModelAverageMiss, int BeatModel, int ModelWon);

/// <param name="Suggestions">Followed running shows with a next season not called yet, soonest premiere first.</param>
public record MyCallsDto(CallRecordDto Record, IReadOnlyList<CallDto> Calls, IReadOnlyList<CallDto> Suggestions);

public record SetCallRequest(double Rating);

/// <summary>
/// Call it: predict a season's average episode rating before it premieres, and see whether you beat the forecast model
/// (<see cref="RatingForecast"/>) once it's over.
/// <list type="bullet">
/// <item><b>Which season:</b> a running show's next one, the season after the latest to air. A call can be made or
/// changed until its first episode airs; then it's locked.</item>
/// <item><b>No anchoring:</b> the model's forecast and the crowd's average are shown only after the caller has called.
/// The model's rating is kept with the call as it stood then, since the forecast moves as the show airs.</item>
/// <item><b>Scoring</b> is Analytics.Worker's (<c>SeasonCallScorer</c>): the season's average provider rating once it's
/// over. A miss is the distance from it; beating the model is being closer than it was.</item>
/// <item><b>The privacy rule:</b> the crowd's average needs 5 or more callers.</item>
/// </list>
/// </summary>
public static class CallItApi
{
    private const int MaxSuggestions = 12;

    public static void MapCallItApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{showId:guid}/call", GetShowAsync).RequireFeature(FeatureFlags.CallIt);
        api.MapPut("/shows/{showId:guid}/call", SetAsync).RequireFeature(FeatureFlags.CallIt);
        api.MapDelete("/shows/{showId:guid}/call", DeleteAsync).RequireFeature(FeatureFlags.CallIt);
        api.MapGet("/me/calls", GetMineAsync).RequireFeature(FeatureFlags.CallIt);
    }

    private static async Task<Results<Ok<ShowCallDto>, NotFound>> GetShowAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await db.Shows.AsNoTracking().FirstOrDefaultAsync(s => s.ShowId == showId, ct) is not { } show)
        {
            return TypedResults.NotFound();
        }
        var now = time.GetUtcNow();
        var calls = await db.SeasonCalls.AsNoTracking().Where(c => c.UserId == userId && c.ShowId == showId).ToListAsync(ct);
        (int Number, DateTimeOffset? PremieresAtUtc)? open = Running(show) ? await OpenSeasonAsync(db, showId, now, ct) : null;
        if (open is null && calls.Count == 0)
        {
            return TypedResults.NotFound();
        }

        var mine = open is { } o ? calls.FirstOrDefault(c => c.SeasonNumber == o.Number) : null;
        CallRangeDto? model = null;
        CrowdCallDto? crowd = null;
        if (mine is not null)
        {
            model = await ModelAsync(db, showId, mine.SeasonNumber, ct);
            crowd = await CrowdAsync(db, showId, mine.SeasonNumber, ct);
        }

        var premieres = await PremieresAsync(db, [showId], now, ct);
        var past = calls.Where(c => c != mine).OrderByDescending(c => c.SeasonNumber)
            .Select(c => ToDto(c, show, premieres.GetValueOrDefault((showId, c.SeasonNumber)), now)).ToList();
        return TypedResults.Ok(new ShowCallDto(open?.Number, open?.PremieresAtUtc, mine?.Rating, model, crowd, past));
    }

    private static async Task<Results<Ok<ShowCallDto>, NotFound, ValidationProblem>> SetAsync(
        Guid showId, SetCallRequest request, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (request.Rating is < 1 or > 10 || double.IsNaN(request.Rating))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["rating"] = ["A call is a rating from 1 to 10."] });
        }
        var now = time.GetUtcNow();
        if (await db.Shows.AsNoTracking().FirstOrDefaultAsync(s => s.ShowId == showId, ct) is not { } show || !Running(show))
        {
            return TypedResults.NotFound();
        }
        var open = await OpenSeasonAsync(db, showId, now, ct);

        var model = await ModelAsync(db, showId, open.Number, ct);
        var call = await db.SeasonCalls.FindAsync([userId, showId, open.Number], ct);
        if (call is null)
        {
            call = new SeasonCall { UserId = userId, ShowId = showId, SeasonNumber = open.Number };
            db.SeasonCalls.Add(call);
        }
        call.Rating = Math.Round(request.Rating, 1);
        call.ModelRating = model?.Rating;
        call.CalledAtUtc = now.UtcDateTime;
        await db.SaveChangesAsync(ct);

        // The panel after a call, from what's already here: the crowd is the one thing left to ask for.
        var crowd = await CrowdAsync(db, showId, open.Number, ct);
        var premieres = await PremieresAsync(db, [showId], now, ct);
        var past = (await db.SeasonCalls.AsNoTracking().Where(c => c.UserId == userId && c.ShowId == showId && c.SeasonNumber != open.Number).ToListAsync(ct))
            .OrderByDescending(c => c.SeasonNumber)
            .Select(c => ToDto(c, show, premieres.GetValueOrDefault((showId, c.SeasonNumber)), now)).ToList();
        return TypedResults.Ok(new ShowCallDto(open.Number, open.PremieresAtUtc, call.Rating, model, crowd, past));
    }

    /// <summary>Takes back a call on the open season; one on a season that has premiered stays.</summary>
    private static async Task<NoContent> DeleteAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var open = await OpenSeasonAsync(db, showId, time.GetUtcNow(), ct);
        await db.SeasonCalls.Where(c => c.UserId == userId && c.ShowId == showId && c.SeasonNumber == open.Number).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<MyCallsDto>> GetMineAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var calls = await db.SeasonCalls.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(ct);
        var followed = await db.Follows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.ShowId).ToListAsync(ct);
        var showIds = calls.Select(c => c.ShowId).Concat(followed).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        // The next season of each followed running show: the one after the latest to air.
        var latest = await db.Episodes.AsNoTracking()
            .Where(e => followed.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
            .GroupBy(e => e.ShowId).Select(g => new { ShowId = g.Key, Season = g.Max(e => e.SeasonNumber) })
            .ToDictionaryAsync(g => g.ShowId, g => g.Season, ct);
        var called = calls.Select(c => (c.ShowId, c.SeasonNumber)).ToHashSet();
        var candidates = followed.Where(id => shows.TryGetValue(id, out var s) && Running(s) && latest.ContainsKey(id))
            .Select(id => (ShowId: id, Season: latest[id] + 1))
            .Where(c => !called.Contains((c.ShowId, c.Season)))
            .ToList();

        var premieres = await PremieresAsync(db, showIds, now, ct);
        var suggestions = candidates
            .Select(c => new CallDto(c.ShowId, shows[c.ShowId].Title, shows[c.ShowId].PosterUrl, c.Season, 0, null, null, "open", null,
                premieres.GetValueOrDefault((c.ShowId, c.Season))))
            .OrderBy(c => c.PremieresAtUtc is null).ThenBy(c => c.PremieresAtUtc).ThenByDescending(c => shows[c.ShowId].Popularity)
            .Take(MaxSuggestions)
            .ToList();

        var dtos = calls.Where(c => shows.ContainsKey(c.ShowId))
            .Select(c => ToDto(c, shows[c.ShowId], premieres.GetValueOrDefault((c.ShowId, c.SeasonNumber)), now))
            .OrderBy(c => c.State switch { "airing" => 0, "open" => 1, _ => 2 })
            .ThenBy(c => c.PremieresAtUtc ?? DateTimeOffset.MaxValue)
            .ToList();

        var scored = calls.Where(c => c.Actual is not null).ToList();
        var withModel = scored.Where(c => c.ModelRating is not null).ToList();
        var record = new CallRecordDto(
            scored.Count,
            scored.Count > 0 ? Math.Round(scored.Average(c => Math.Abs(c.Rating - c.Actual!.Value)), 2) : null,
            withModel.Count > 0 ? Math.Round(withModel.Average(c => Math.Abs(c.ModelRating!.Value - c.Actual!.Value)), 2) : null,
            withModel.Count(c => Beat(c) == true),
            withModel.Count(c => Beat(c) == false));
        return TypedResults.Ok(new MyCallsDto(record, dtos, suggestions));
    }

    private static bool Running(ShowCopy show) => !show.HasEnded();

    /// <summary>Everyone's average call on the season, with the privacy rule (5+ callers, the caller's own included).</summary>
    private static async Task<CrowdCallDto?> CrowdAsync(AnalyticsContext db, Guid showId, int season, CancellationToken ct)
    {
        var everyone = await db.SeasonCalls.AsNoTracking().Where(c => c.ShowId == showId && c.SeasonNumber == season)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Average = g.Average(c => c.Rating) }).FirstOrDefaultAsync(ct);
        return everyone is { Count: >= InsightRules.MinUsersToShow } ? new CrowdCallDto(Math.Round(everyone.Average, 1), everyone.Count) : null;
    }

    /// <summary>The season after the latest to air (the first, for a show yet to air), and its first dated episode.</summary>
    private static async Task<(int Number, DateTimeOffset? PremieresAtUtc)> OpenSeasonAsync(AnalyticsContext db, Guid showId, DateTimeOffset now,
        CancellationToken ct)
    {
        var season = await db.Episodes.AsNoTracking().Where(e => e.ShowId == showId && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
            .MaxAsync(e => (int?)e.SeasonNumber, ct) ?? 0;
        var premiere = await db.Episodes.AsNoTracking().Where(e => e.ShowId == showId && e.SeasonNumber == season + 1 && e.AirsAtUtc != null)
            .MinAsync(e => e.AirsAtUtc, ct);
        return (season + 1, premiere);
    }

    /// <summary>Each season's first dated episode, for the given shows.</summary>
    private static async Task<Dictionary<(Guid, int), DateTimeOffset?>> PremieresAsync(AnalyticsContext db, IReadOnlyCollection<Guid> showIds,
        DateTimeOffset now, CancellationToken ct) =>
        (await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc != null)
            .GroupBy(e => new { e.ShowId, e.SeasonNumber })
            .Select(g => new { g.Key.ShowId, g.Key.SeasonNumber, First = g.Min(e => e.AirsAtUtc)!.Value })
            .ToListAsync(ct))
        .ToDictionary(g => (g.ShowId, g.SeasonNumber), g => (DateTimeOffset?)g.First);

    /// <summary>The show document's rating forecast, when it's for this season.</summary>
    private static async Task<CallRangeDto?> ModelAsync(AnalyticsContext db, Guid showId, int season, CancellationToken ct)
    {
        return (await ShowDocuments.LoadAsync(db, showId, ct))?.Outlook?.NextSeasonRating is { } f && f.SeasonNumber == season
            ? new CallRangeDto(f.Rating, f.Low, f.High)
            : null;
    }

    private static CallDto ToDto(SeasonCall call, ShowCopy show, DateTimeOffset? premieres, DateTimeOffset now) =>
        new(call.ShowId, show.Title, show.PosterUrl, call.SeasonNumber, call.Rating, call.ModelRating, call.Actual,
            call.Actual is not null ? "scored" : premieres is { } p && p <= now ? "airing" : "open",
            Beat(call), premieres);

    /// <summary>Whether the caller was closer to the actual rating than the model; null unscored, without a model, or tied.</summary>
    private static bool? Beat(SeasonCall call)
    {
        if (call.Actual is not { } actual || call.ModelRating is not { } model)
        {
            return null;
        }
        var mine = Math.Round(Math.Abs(call.Rating - actual), 2);
        var theirs = Math.Round(Math.Abs(model - actual), 2);
        return mine == theirs ? null : mine < theirs;
    }
}
