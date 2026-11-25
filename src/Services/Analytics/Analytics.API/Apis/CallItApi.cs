using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

public record SetCallRequest(double Rating);

/// <summary>
/// Call it (see <see cref="Calls"/>): the caller's calls are kept here; the scoring rules are the engine's.
/// </summary>
public static class CallItApi
{
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

        var premieres = await PremieresAsync(db, [showId], ct);
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
        var premieres = await PremieresAsync(db, [showId], ct);
        var past = (await db.SeasonCalls.AsNoTracking().Where(c => c.UserId == userId && c.ShowId == showId && c.SeasonNumber != open.Number).ToListAsync(ct))
            .OrderByDescending(c => c.SeasonNumber)
            .Select(c => ToDto(c, show, premieres.GetValueOrDefault((showId, c.SeasonNumber)), now)).ToList();
        return TypedResults.Ok(new ShowCallDto(open.Number, open.PremieresAtUtc, call.Rating, model, crowd, past));
    }

    /// <summary>
    /// Takes back a call on the open season; one on a season that has premiered stays.
    /// </summary>
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
            .AiredRegular(now).Where(e => followed.Contains(e.ShowId))
            .GroupBy(e => e.ShowId).Select(g => new { ShowId = g.Key, Season = g.Max(e => e.SeasonNumber) })
            .ToDictionaryAsync(g => g.ShowId, g => g.Season, ct);
        var called = calls.Select(c => (c.ShowId, c.SeasonNumber)).ToHashSet();
        var candidates = followed.Where(id => shows.TryGetValue(id, out var s) && Running(s) && latest.ContainsKey(id))
            .Select(id => (ShowId: id, Season: latest[id] + 1))
            .Where(c => !called.Contains((c.ShowId, c.Season)))
            .ToList();

        var premieres = await PremieresAsync(db, showIds, ct);
        var suggestions = candidates
            .Select(c => new CallDto(c.ShowId, shows[c.ShowId].Title, shows[c.ShowId].PosterUrl, c.Season, 0, null, null, "open", null,
                premieres.GetValueOrDefault((c.ShowId, c.Season))))
            .OrderBy(c => c.PremieresAtUtc is null).ThenBy(c => c.PremieresAtUtc).ThenByDescending(c => shows[c.ShowId].Popularity)
            .Take(Calls.MaxSuggestions)
            .ToList();

        var dtos = Calls.Ordered(calls.Where(c => shows.ContainsKey(c.ShowId))
            .Select(c => ToDto(c, shows[c.ShowId], premieres.GetValueOrDefault((c.ShowId, c.SeasonNumber)), now)));
        return TypedResults.Ok(new MyCallsDto(Calls.Record(calls.Select(c => (c.Rating, c.ModelRating, c.Actual)).ToList()), dtos, suggestions));
    }

    private static bool Running(ShowCopy show) => !show.HasEnded();

    /// <summary>
    /// Everyone's average call on the season, with the privacy rule (5+ callers, the caller's own included).
    /// </summary>
    private static async Task<CrowdCallDto?> CrowdAsync(AnalyticsContext db, Guid showId, int season, CancellationToken ct)
    {
        var everyone = await db.SeasonCalls.AsNoTracking().Where(c => c.ShowId == showId && c.SeasonNumber == season)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Average = g.Average(c => c.Rating) }).FirstOrDefaultAsync(ct);
        return everyone is null ? null : Calls.Crowd(everyone.Count, everyone.Average);
    }

    /// <summary>
    /// The season after the latest to air (the first, for a show yet to air), and its first dated episode.
    /// </summary>
    private static async Task<(int Number, DateTimeOffset? PremieresAtUtc)> OpenSeasonAsync(AnalyticsContext db, Guid showId, DateTimeOffset now,
        CancellationToken ct)
    {
        var season = await db.Episodes.AsNoTracking().AiredRegular(now).Where(e => e.ShowId == showId)
            .MaxAsync(e => (int?)e.SeasonNumber, ct) ?? 0;
        var premiere = await db.Episodes.AsNoTracking().Where(e => e.ShowId == showId && e.SeasonNumber == season + 1 && e.AirsAtUtc != null)
            .MinAsync(e => e.AirsAtUtc, ct);
        return (season + 1, premiere);
    }

    /// <summary>
    /// Each season's first dated episode, for the given shows.
    /// </summary>
    private static async Task<Dictionary<(Guid, int), DateTimeOffset?>> PremieresAsync(AnalyticsContext db, IReadOnlyCollection<Guid> showIds,
        CancellationToken ct) =>
        (await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc != null)
            .GroupBy(e => new { e.ShowId, e.SeasonNumber })
            .Select(g => new { g.Key.ShowId, g.Key.SeasonNumber, First = g.Min(e => e.AirsAtUtc)!.Value })
            .ToListAsync(ct))
        .ToDictionary(g => (g.ShowId, g.SeasonNumber), g => (DateTimeOffset?)g.First);

    /// <summary>
    /// The show document's rating forecast, when it's for this season.
    /// </summary>
    private static async Task<CallRangeDto?> ModelAsync(AnalyticsContext db, Guid showId, int season, CancellationToken ct)
    {
        return (await ShowDocuments.LoadAsync(db, showId, ct))?.Outlook?.NextSeasonRating is { } f && f.SeasonNumber == season
            ? new CallRangeDto(f.Rating, f.Low, f.High)
            : null;
    }

    private static CallDto ToDto(SeasonCall call, ShowCopy show, DateTimeOffset? premieres, DateTimeOffset now) =>
        new(call.ShowId, show.Title, show.PosterUrl, call.SeasonNumber, call.Rating, call.ModelRating, call.Actual,
            Calls.State(call.Actual, premieres, now), Calls.Beat(call.Rating, call.ModelRating, call.Actual), premieres);
}
