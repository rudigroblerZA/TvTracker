using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Analytics.API.Services;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="Nameable">The caller's shows a result could name, heaviest first: what they may take out before their side is used.</param>
public record MatchStartedDto(string Token, IReadOnlyList<MatchShowDto> Nameable);

/// <param name="State">"open" (waiting for the friend) or "done".</param>
/// <param name="You">"inviter" or "friend" when the caller is signed in as one of the two; null otherwise.</param>
/// <param name="Result">The result, once done.</param>
/// <param name="Nameable">For a signed-in friend on an open match: their shows a result could name. Null otherwise.</param>
/// <param name="GuestKey">Given once, to the guest who just answered: lets that device delete the match.</param>
public record MatchViewDto(string Token, string State, string? InviterName, string? FriendName, string? You, TasteMatchDto? Result,
    IReadOnlyList<MatchShowDto>? Nameable = null, string? GuestKey = null);

/// <param name="Score">The result's score, once done.</param>
public record MyMatchDto(string Token, string State, string Role, string? OtherName, int? Score, DateTime CreatedAtUtc, DateTime? DoneAtUtc);

/// <param name="Name">The guest's first name, optional; shown on the result.</param>
public record MatchPicksRequest(IReadOnlyList<Guid> ShowIds, string? Name);

/// <param name="Unnamed">Shows of the caller's a result must not name.</param>
public record MatchUnnamedRequest(IReadOnlyList<Guid> Unnamed);

/// <summary>
/// Taste Match: a member starts a match and sends its link; a friend answers, signed in or not; both see the result.
/// This only stores matches and loads each side; the score and what a result may name are the engine's
/// (<see cref="TasteMatch"/>), and the comparison by meaning is the catalog's (<see cref="CatalogMatchClient"/>).
/// <list type="bullet">
/// <item><b>The link is the key.</b> Opening a match and answering it as a guest need no sign-in; they are rate limited
/// by address, as shared lists' links are. An unknown, expired or deleted token all read the same: 404.</item>
/// <item><b>One answer.</b> The first answer makes the match done; the result is stored and never recomputed.</item>
/// <item><b>Sides aren't stored</b> for members: they are read when the friend answers.</item>
/// <item><b>Expiry:</b> an open link after 30 days, a done match after a year. Expired matches read as gone at once and
/// are deleted the next time anyone starts a match.</item>
/// </list>
/// </summary>
public static class TasteMatchApi
{
    /// <summary>
    /// The most links a member may have waiting for a friend.
    /// </summary>
    private const int MaxOpen = 20;

    private const int MaxNameLength = 30;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The calls that need a member; mapped inside the signed-in group.
    /// </summary>
    public static void MapTasteMatchApi(this RouteGroupBuilder api)
    {
        api.MapPost("/me/matches", StartAsync).RequireFeature(FeatureFlags.TasteMatch);
        api.MapGet("/me/matches", MineAsync).RequireFeature(FeatureFlags.TasteMatch);
        api.MapPut("/me/matches/{token}/unnamed", SetUnnamedAsync).RequireFeature(FeatureFlags.TasteMatch);
        api.MapPost("/matches/{token}/accept", AcceptAsync).RequireFeature(FeatureFlags.TasteMatch);
    }

    /// <summary>
    /// The calls anyone with the link may make.
    /// </summary>
    public static void MapTasteMatchLinkApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/matches").AllowAnonymous().RequireRateLimiting(ClientRateLimiting.LinkedList).RequireFeature(FeatureFlags.TasteMatch);
        api.MapGet("/{token}", OpenAsync);
        api.MapPost("/{token}/picks", PickAsync);
        api.MapDelete("/{token}", DeleteAsync);
    }

    private static async Task<Results<Ok<MatchStartedDto>, ProblemHttpResult>> StartAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow().UtcDateTime;
        // The sweep: nothing else deletes an expired match, and a match can only be started here.
        await db.TasteMatches.Where(m => m.ExpiresAtUtc < now).ExecuteDeleteAsync(ct);

        var side = TasteMatch.Side((await ReunionsApi.LoveAsync(db, userId, ct)).Loved);
        if (side.Count < TasteMatch.MinLoved)
        {
            return Problem("too-few-shows", $"Rate or follow {TasteMatch.MinLoved - side.Count} more {(TasteMatch.MinLoved - side.Count == 1 ? "show" : "shows")} first: a match needs {TasteMatch.MinLoved} you love.");
        }
        if (await db.TasteMatches.CountAsync(m => m.InviterUserId == userId && m.Result == null, ct) >= MaxOpen)
        {
            return Problem("too-many-open", $"You have {MaxOpen} matches waiting for a friend. Cancel one to start another.");
        }

        var match = new TasteMatchRow
        {
            Token = NewToken(),
            InviterUserId = userId,
            InviterDisplayName = FirstName(user.GetDisplayName()),
            CreatedAtUtc = now,
            ExpiresAtUtc = now + TasteMatchRow.OpenFor
        };
        db.TasteMatches.Add(match);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new MatchStartedDto(match.Token, await NameableAsync(db, side, ct)));
    }

    private static async Task<Ok<List<MyMatchDto>>> MineAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow().UtcDateTime;
        var matches = await db.TasteMatches.AsNoTracking()
            .Where(m => (m.InviterUserId == userId || m.FriendUserId == userId) && m.ExpiresAtUtc >= now)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToListAsync(ct);
        return TypedResults.Ok(matches.Select(m =>
        {
            var inviter = m.InviterUserId == userId;
            return new MyMatchDto(m.Token, StateOf(m), inviter ? "inviter" : "friend", inviter ? m.FriendDisplayName : m.InviterDisplayName,
                ResultOf(m)?.Score, m.CreatedAtUtc, m.DoneAtUtc);
        }).ToList());
    }

    /// <summary>
    /// The inviter's shows not to name, while the match is still open.
    /// </summary>
    private static async Task<Results<NoContent, NotFound>> SetUnnamedAsync(
        string token, MatchUnnamedRequest request, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await FindAsync(db, token, time, ct, tracked: true) is not { Result: null } match || match.InviterUserId != userId)
        {
            return TypedResults.NotFound();
        }

        match.InviterUnnamed = request.Unnamed.Distinct().Take(TasteMatch.MaxScored).ToList();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<MatchViewDto>, NotFound>> OpenAsync(
        string token, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        if (await FindAsync(db, token, time, ct) is not { } match)
        {
            return TypedResults.NotFound();
        }

        // A signed-in friend sees what of theirs could be named before they answer.
        IReadOnlyList<MatchShowDto>? nameable = null;
        if (match.Result is null && UserIdOf(user) is { } userId && userId != match.InviterUserId)
        {
            nameable = await NameableAsync(db, TasteMatch.Side((await ReunionsApi.LoveAsync(db, userId, ct)).Loved), ct);
        }
        return TypedResults.Ok(View(match, user, nameable));
    }

    /// <summary>
    /// A guest's answer: the shows they picked, and a first name if they gave one.
    /// </summary>
    private static async Task<Results<Ok<MatchViewDto>, NotFound, ProblemHttpResult>> PickAsync(
        string token, MatchPicksRequest request, ClaimsPrincipal user, AnalyticsContext db, CatalogMatchClient catalog, TimeProvider time,
        CancellationToken ct)
    {
        if (await FindAsync(db, token, time, ct, tracked: true) is not { Result: null } match)
        {
            return TypedResults.NotFound();
        }

        var asked = request.ShowIds.Distinct().Take(TasteMatch.MaxPicks).ToList();
        var picks = await db.Shows.AsNoTracking().Where(s => asked.Contains(s.ShowId)).Select(s => s.ShowId).ToListAsync(ct);
        if (picks.Count < TasteMatch.MinPicks)
        {
            return Problem("too-few-picks", $"Pick at least {TasteMatch.MinPicks} shows.");
        }

        var side = TasteMatch.Picked(picks);
        if (await ResultAsync(db, catalog, match, side, picks, ct) is not { } result)
        {
            return Problem("pick-another", "One of those is too new to compare yet. Pick one more show.");
        }

        match.GuestPicks = picks;
        match.GuestKey = NewToken();
        match.FriendDisplayName = FirstName(request.Name);
        Finish(match, result, time);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(View(match, user) with { GuestKey = match.GuestKey });
    }

    /// <summary>
    /// A member friend's answer: their own shows, less the ones they asked not to have named.
    /// </summary>
    private static async Task<Results<Ok<MatchViewDto>, NotFound, ProblemHttpResult>> AcceptAsync(
        string token, MatchUnnamedRequest? request, ClaimsPrincipal user, AnalyticsContext db, CatalogMatchClient catalog, TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await FindAsync(db, token, time, ct, tracked: true) is not { Result: null } match)
        {
            return TypedResults.NotFound();
        }
        if (match.InviterUserId == userId)
        {
            return Problem("own-match", "This is your own match: send the link to a friend.");
        }

        var (known, loved) = await ReunionsApi.LoveAsync(db, userId, ct);
        var side = TasteMatch.Side(loved);
        if (side.Count < TasteMatch.MinPicks)
        {
            return Problem("too-few-shows", $"Rate or follow a few shows first, or sign out and pick {TasteMatch.MinPicks} for this match.");
        }

        match.FriendUnnamed = (request?.Unnamed ?? []).Distinct().Take(TasteMatch.MaxScored).ToList();
        if (await ResultAsync(db, catalog, match, side, known, ct) is not { } result)
        {
            return Problem("pick-another", "Too few of your shows can be compared yet. Try again in a few minutes.");
        }

        match.FriendUserId = userId;
        match.FriendDisplayName = FirstName(user.GetDisplayName());
        Finish(match, result, time);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(View(match, user));
    }

    /// <summary>
    /// Deletes a match: the inviter, the friend when signed in, or the guest from the device that answered.
    /// </summary>
    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        string token, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, [FromQuery] string? key = null, CancellationToken ct = default)
    {
        if (await FindAsync(db, token, time, ct, tracked: true) is not { } match)
        {
            return TypedResults.NotFound();
        }

        var userId = UserIdOf(user);
        var allowed = (userId is not null && (userId == match.InviterUserId || userId == match.FriendUserId))
            || (match.GuestKey is { } guestKey && key is not null
                && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(guestKey), System.Text.Encoding.UTF8.GetBytes(key)));
        if (!allowed)
        {
            // The same answer as an unknown token: who may delete says nothing about whether a match exists.
            return TypedResults.NotFound();
        }

        db.TasteMatches.Remove(match);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The result for a match and the friend's side; null when the catalog can't compare the two yet (a show with no
    /// vector leaves a side with too few).
    /// </summary>
    private static async Task<TasteMatchDto?> ResultAsync(AnalyticsContext db, CatalogMatchClient catalog, TasteMatchRow match,
        IReadOnlyDictionary<Guid, double> friend, IReadOnlyCollection<Guid> friendKnown, CancellationToken ct)
    {
        var (inviterKnown, inviterLoved) = await ReunionsApi.LoveAsync(db, match.InviterUserId, ct);
        var inviter = TasteMatch.Side(inviterLoved);

        var compared = await catalog.CompareAsync((inviter, inviterKnown), (friend, friendKnown), ct);
        if (compared.Alike is not { } alike)
        {
            return null;
        }

        var ids = inviter.Keys.Concat(friend.Keys).Concat(compared.Together).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.ShowId))
            .Select(s => new { s.ShowId, s.Title, s.PosterUrl, s.Genres })
            .ToDictionaryAsync(s => s.ShowId, s => new MatchShow(s.Title, s.PosterUrl, s.Genres), ct);
        return TasteMatch.Of(match.Token, inviter, friend, match.InviterUnnamed.Concat(match.FriendUnnamed).ToHashSet(), alike,
            compared.Together, compared.Loose, shows);
    }

    private static void Finish(TasteMatchRow match, TasteMatchDto result, TimeProvider time)
    {
        var now = time.GetUtcNow().UtcDateTime;
        match.Result = JsonSerializer.Serialize(result, Json);
        match.DoneAtUtc = now;
        match.ExpiresAtUtc = now + TasteMatchRow.KeptFor;
    }

    private static async Task<List<MatchShowDto>> NameableAsync(AnalyticsContext db, IReadOnlyDictionary<Guid, double> side, CancellationToken ct)
    {
        var ids = TasteMatch.Nameable(side);
        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.ShowId))
            .Select(s => new { s.ShowId, s.Title, s.PosterUrl }).ToDictionaryAsync(s => s.ShowId, ct);
        return ids.Where(shows.ContainsKey).Select(id => new MatchShowDto(id, shows[id].Title, shows[id].PosterUrl)).ToList();
    }

    /// <summary>
    /// The match with this token, unless it has expired.
    /// </summary>
    private static async Task<TasteMatchRow?> FindAsync(AnalyticsContext db, string token, TimeProvider time, CancellationToken ct, bool tracked = false)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var matches = tracked ? db.TasteMatches : db.TasteMatches.AsNoTracking();
        return await matches.SingleOrDefaultAsync(m => m.Token == token && m.ExpiresAtUtc >= now, ct);
    }

    private static MatchViewDto View(TasteMatchRow match, ClaimsPrincipal user, IReadOnlyList<MatchShowDto>? nameable = null)
    {
        var userId = UserIdOf(user);
        var you = userId is null ? null : userId == match.InviterUserId ? "inviter" : userId == match.FriendUserId ? "friend" : null;
        return new MatchViewDto(match.Token, StateOf(match), match.InviterDisplayName, match.FriendDisplayName, you, ResultOf(match), nameable);
    }

    private static string StateOf(TasteMatchRow match) => match.Result is null ? "open" : "done";

    internal static TasteMatchDto? ResultOf(TasteMatchRow match) =>
        match.Result is null ? null : JsonSerializer.Deserialize<TasteMatchDto>(match.Result, Json);

    /// <summary>
    /// The caller's id when they're signed in as a person; null for a guest.
    /// </summary>
    private static string? UserIdOf(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true ? user.FindFirstValue("sub") : null;

    /// <summary>
    /// A first name fit to show to whoever has the link: the first word, without control characters, cut to
    /// <see cref="MaxNameLength"/>; null when nothing is left.
    /// </summary>
    internal static string? FirstName(string? name)
    {
        var first = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? null : first.Length > MaxNameLength ? first[..MaxNameLength] : first;
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static ProblemHttpResult Problem(string code, string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Not yet", extensions: new Dictionary<string, object?> { ["code"] = code });
}
