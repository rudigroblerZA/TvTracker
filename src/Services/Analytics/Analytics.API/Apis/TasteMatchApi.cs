using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Analytics.API.Services;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Analytics.API.Apis;

/// <param name="Nameable">The caller's shows a result could name, heaviest first: what they may take out before their side is used.</param>
public record MatchStartedDto(string Token, IReadOnlyList<MatchShowDto> Nameable);

/// <param name="State">"open" (waiting for the friend) or "done".</param>
/// <param name="You">"inviter" or "friend" when the caller is signed in as one of the two; null otherwise.</param>
/// <param name="Result">The result, once done.</param>
/// <param name="Nameable">For a signed-in friend on an open match: their shows a result could name. Null otherwise.</param>
/// <param name="GuestKey">Given once, to the guest who just answered: lets that device delete the match, or attach it to an
/// account made afterwards.</param>
/// <param name="OtherUserId">The other member, told only to the two of them and only when both are members: for planning an
/// evening together.</param>
public record MatchViewDto(string Token, string State, string? InviterName, string? FriendName, string? You, TasteMatchDto? Result,
    IReadOnlyList<MatchShowDto>? Nameable = null, string? GuestKey = null, string? OtherUserId = null);

/// <param name="Picks">The shows the guest picked, for offering as follows.</param>
public record MatchClaimedDto(MatchViewDto Match, IReadOnlyList<MatchShowDto> Picks);

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
/// <item><b>One answer.</b> The first answer makes the match done; the result is stored and never recomputed. Two
/// answers at once can't both win: the row carries a version, and the one saved second finds the link gone.</item>
/// <item><b>Blocks:</b> a signed-in caller can't open, answer or claim a match when there is a block between them and
/// either of its two people; it reads as gone, to the inviter too once the friend has blocked them. Social knows who
/// has blocked whom (<see cref="SocialBlocksClient"/>). When Social can't be asked, a match can still be read (without
/// telling either who the other is), but not answered or claimed: that is tried again. A signed-out guest is nobody
/// to block.</item>
/// <item><b>The inviter is told</b> when the friend answers: an event stored with the result, which the worker's outbox
/// sends and Notifications turns into an inbox item.</item>
/// <item><b>Sides aren't stored</b> for members: they are read when the friend answers.</item>
/// <item><b>The card</b> is the result as a picture (<see cref="MatchCard"/>): drawn on first request and kept in memory
/// for a day, since a result never changes. It is only served while its match exists.</item>
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
        api.MapPost("/matches/{token}/claim", ClaimAsync).RequireFeature(FeatureFlags.TasteMatch);
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
        api.MapGet("/{token}/card.png", CardAsync);
    }

    internal static async Task<Results<Ok<MatchStartedDto>, ProblemHttpResult>> StartAsync(
        ClaimsPrincipal user, AnalyticsContext db, MatchMetrics metrics, TimeProvider time, CancellationToken ct)
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
        metrics.Started();
        return TypedResults.Ok(new MatchStartedDto(match.Token, await NameableAsync(db, side, ct)));
    }

    internal static async Task<Ok<List<MyMatchDto>>> MineAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
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
    internal static async Task<Results<NoContent, NotFound>> SetUnnamedAsync(
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

    internal static async Task<Results<Ok<MatchViewDto>, NotFound>> OpenAsync(
        string token, ClaimsPrincipal user, AnalyticsContext db, SocialBlocksClient blocks, ILogger<MatchMetrics> logger, TimeProvider time,
        CancellationToken ct)
    {
        if (await FindAsync(db, token, time, ct) is not { } match)
        {
            return TypedResults.NotFound();
        }
        // Reading goes ahead when Social can't say; the view then tells neither of the two who the other is.
        var blocked = await BlockedAsync(blocks, match, UserIdOf(user), logger, ct);
        if (blocked == true)
        {
            return TypedResults.NotFound();
        }

        // A signed-in friend sees what of theirs could be named before they answer.
        IReadOnlyList<MatchShowDto>? nameable = null;
        if (match.Result is null && UserIdOf(user) is { } userId && userId != match.InviterUserId)
        {
            nameable = await NameableAsync(db, TasteMatch.Side((await ReunionsApi.LoveAsync(db, userId, ct)).Loved), ct);
        }
        return TypedResults.Ok(View(match, user, nameable, tellOther: blocked == false));
    }

    /// <summary>
    /// A guest's answer: the shows they picked, and a first name if they gave one.
    /// </summary>
    internal static async Task<Results<Ok<MatchViewDto>, NotFound, ProblemHttpResult>> PickAsync(
        string token, MatchPicksRequest request, ClaimsPrincipal user, AnalyticsContext db, CatalogMatchClient catalog, SocialBlocksClient blocks,
        MatchMetrics metrics, ILogger<MatchMetrics> logger, TimeProvider time, CancellationToken ct)
    {
        if (await FindAsync(db, token, time, ct, tracked: true) is not { Result: null } match)
        {
            return TypedResults.NotFound();
        }
        // Picking shows instead of using one's own doesn't get a signed-in member round a block.
        if (await BlockedAsync(blocks, match, UserIdOf(user), logger, ct) is not false and var blocked)
        {
            return blocked == true ? TypedResults.NotFound() : TryAgain();
        }

        var asked = request.ShowIds.Distinct().Take(TasteMatch.MaxPicks).ToList();
        // The ones that are shows, in the order they were picked.
        var known = await db.Shows.AsNoTracking().Where(s => asked.Contains(s.ShowId)).Select(s => s.ShowId).ToHashSetAsync(ct);
        var picks = asked.Where(known.Contains).ToList();
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
        Finish(db, match, result, time);
        if (!await SaveIfUnchangedAsync(db, ct))
        {
            return TypedResults.NotFound();
        }
        metrics.Answered(guest: true);
        return TypedResults.Ok(View(match, user) with { GuestKey = match.GuestKey });
    }

    /// <summary>
    /// A member friend's answer: their own shows, less the ones they asked not to have named.
    /// </summary>
    internal static async Task<Results<Ok<MatchViewDto>, NotFound, ProblemHttpResult>> AcceptAsync(
        string token, MatchUnnamedRequest? request, ClaimsPrincipal user, AnalyticsContext db, CatalogMatchClient catalog, SocialBlocksClient blocks,
        MatchMetrics metrics, ILogger<MatchMetrics> logger, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await FindAsync(db, token, time, ct, tracked: true) is not { Result: null } match)
        {
            return TypedResults.NotFound();
        }
        if (await BlockedAsync(blocks, match, userId, logger, ct) is not false and var blocked)
        {
            return blocked == true ? TypedResults.NotFound() : TryAgain();
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
        Finish(db, match, result, time);
        if (!await SaveIfUnchangedAsync(db, ct))
        {
            return TypedResults.NotFound();
        }
        metrics.Answered(guest: false);
        return TypedResults.Ok(View(match, user, tellOther: true));
    }

    /// <summary>
    /// A guest who answered and then made an account attaches the match to it: they become its friend, as if they had
    /// answered signed in, and are handed their picks to follow if they wish. Needs the key they were given when they
    /// answered. Nothing else is carried over: the picks stay picks until they follow them.
    /// </summary>
    internal static async Task<Results<Ok<MatchClaimedDto>, NotFound, ProblemHttpResult>> ClaimAsync(
        string token, [FromQuery] string key, ClaimsPrincipal user, AnalyticsContext db, SocialBlocksClient blocks, MatchMetrics metrics,
        ILogger<MatchMetrics> logger, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await FindAsync(db, token, time, ct, tracked: true) is not { Result: not null, FriendUserId: null, GuestKey: { } guestKey } match
            || !SameKey(guestKey, key) || match.InviterUserId == userId)
        {
            return TypedResults.NotFound();
        }
        if (await BlockedAsync(blocks, match, userId, logger, ct) is not false and var blocked)
        {
            return blocked == true ? TypedResults.NotFound() : TryAgain();
        }

        match.FriendUserId = userId;
        // The device that answered is now signed in: the key has done its work.
        match.GuestKey = null;
        if (!await SaveIfUnchangedAsync(db, ct))
        {
            return TypedResults.NotFound();
        }
        metrics.Claimed();

        var picks = match.GuestPicks;
        var shows = await db.Shows.AsNoTracking().Where(s => picks.Contains(s.ShowId))
            .Select(s => new { s.ShowId, s.Title, s.PosterUrl }).ToDictionaryAsync(s => s.ShowId, ct);
        return TypedResults.Ok(new MatchClaimedDto(View(match, user, tellOther: true),
            picks.Where(shows.ContainsKey).Select(id => new MatchShowDto(id, shows[id].Title, shows[id].PosterUrl)).ToList()));
    }

    /// <summary>
    /// Deletes a match: the inviter, the friend when signed in, or the guest from the device that answered.
    /// </summary>
    internal static async Task<Results<NoContent, NotFound>> DeleteAsync(
        string token, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, [FromQuery] string? key = null, CancellationToken ct = default)
    {
        if (await FindAsync(db, token, time, ct, tracked: true) is not { } match)
        {
            return TypedResults.NotFound();
        }

        var userId = UserIdOf(user);
        var allowed = (userId is not null && (userId == match.InviterUserId || userId == match.FriendUserId))
            || (match.GuestKey is { } guestKey && key is not null && SameKey(guestKey, key));
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
    /// A done match's share card. <paramref name="size"/> is "story" (1080×1920) or "link" (1200×630, the default: what a
    /// pasted link shows). 404 for a match that isn't done, as for one that doesn't exist.
    /// </summary>
    internal static async Task<Results<FileContentHttpResult, NotFound>> CardAsync(
        string token, AnalyticsContext db, HybridCache cache, IHttpClientFactory clients, Branding brand, TimeProvider time, HttpContext http,
        string? size = null, CancellationToken ct = default)
    {
        // Read every time, so a deleted match's card stops being served at once; only the drawing is kept.
        if (await FindAsync(db, token, time, ct) is not { Result: not null } match || ResultOf(match) is not { } result)
        {
            return TypedResults.NotFound();
        }

        var shape = string.Equals(size, "story", StringComparison.OrdinalIgnoreCase) ? MatchCardSize.Story : MatchCardSize.Link;
        var png = await cache.GetOrCreateAsync($"analytics:match-card:{match.Token}:{shape}:{brand.AppName}", async cancel =>
            {
                // What the two share when there is any; else what they could watch together. Never one side's own shows.
                var (shows, label) = result.BothLove.Count > 0
                    ? (result.BothLove.Select(s => s.PosterUrl), "You both love")
                    : (result.WatchTogether.Select(s => s.PosterUrl), "Watch together");
                var posters = await Task.WhenAll(shows.OfType<string>().Take(MatchCard.MaxPosters).Select(url => PosterAsync(clients, url, cancel)));
                var drawn = posters.OfType<byte[]>().ToList();
                return MatchCard.Render(new MatchCardContent(MatchCard.NameOr(match.InviterDisplayName, "A friend"), MatchCard.NameOr(match.FriendDisplayName, "A friend"),
                    result.Score, result.Line, drawn, drawn.Count > 0 ? label : null, brand.AppName), shape);
            },
            new HybridCacheEntryOptions { Expiration = CardKeptFor, LocalCacheExpiration = CardKeptFor }, cancellationToken: ct);

        // Short, so a deleted match's card doesn't outlive it by long in a browser or a preview service.
        http.Response.Headers.CacheControl = "public, max-age=600";
        return TypedResults.File(png, "image/png");
    }

    private static readonly TimeSpan CardKeptFor = TimeSpan.FromDays(1);

    /// <summary>
    /// The largest poster read for a card.
    /// </summary>
    private const int MaxPosterBytes = 4 * 1024 * 1024;

    public const string PosterClient = "match-posters";

    /// <summary>
    /// A poster's bytes, or null when it can't be had quickly: the card is drawn without it. Only https addresses are
    /// fetched (they come from the catalog's provider, and nothing else should be reachable from here).
    /// </summary>
    private static async Task<byte[]?> PosterAsync(IHttpClientFactory clients, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        try
        {
            using var response = await clients.CreateClient(PosterClient).GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxPosterBytes
                || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true)
            {
                return null;
            }
            // Read no further than the cap: a body with no declared length can be any size.
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var kept = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (kept.Length + read > MaxPosterBytes)
                {
                    return null;
                }
                kept.Write(buffer, 0, read);
            }
            return kept.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
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

    /// <summary>
    /// Makes the match done, and stores the news for the inviter with it: saved together, sent by the worker's outbox.
    /// </summary>
    private static void Finish(AnalyticsContext db, TasteMatchRow match, TasteMatchDto result, TimeProvider time)
    {
        var now = time.GetUtcNow().UtcDateTime;
        match.Result = JsonSerializer.Serialize(result, Json);
        match.DoneAtUtc = now;
        match.ExpiresAtUtc = now + TasteMatchRow.KeptFor;
        db.IntegrationEventLogs.Add(IntegrationEventLogEntry.For(
            new TasteMatchAnsweredIntegrationEvent(match.InviterUserId, match.Token, match.FriendDisplayName, result.Score)));
    }

    /// <summary>
    /// Whether there is a block between a signed-in caller and either of the match's two people (other than themselves):
    /// so the inviter is checked against a member friend as the friend is against the inviter. False for a guest. Null
    /// when Social couldn't be asked: the caller decides what may still go ahead.
    /// </summary>
    private static async Task<bool?> BlockedAsync(SocialBlocksClient blocks, TasteMatchRow match, string? userId, ILogger logger, CancellationToken ct)
    {
        if (userId is null)
        {
            return false;
        }
        try
        {
            foreach (var other in new[] { match.InviterUserId, match.FriendUserId }.OfType<string>().Where(id => id != userId))
            {
                if (await blocks.BlockedBetweenAsync(other, userId, ct))
                {
                    return true;
                }
            }
            return false;
        }
        // Anything but the caller going away: Social down or slow, its circuit open, the service token refused.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Couldn't ask Social about blocks for a taste match");
            return null;
        }
    }

    /// <summary>
    /// Saves, unless someone else changed the match first (two answers at once): then nothing is saved, and the caller
    /// treats the link as gone.
    /// </summary>
    private static async Task<bool> SaveIfUnchangedAsync(AnalyticsContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static ProblemHttpResult TryAgain() =>
        TypedResults.Problem("Couldn't check that just now. Try again in a moment.", statusCode: StatusCodes.Status503ServiceUnavailable, title: "Try again");

    private static bool SameKey(string expected, string given) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(given));

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

    /// <param name="tellOther">Whether each of two members may be told who the other is: only once it's known there is no
    /// block between them.</param>
    private static MatchViewDto View(TasteMatchRow match, ClaimsPrincipal user, IReadOnlyList<MatchShowDto>? nameable = null, bool tellOther = false)
    {
        var userId = UserIdOf(user);
        var you = userId is null ? null : userId == match.InviterUserId ? "inviter" : userId == match.FriendUserId ? "friend" : null;
        // Each of two members is told who the other is; nobody else is, and a guest has no id to tell.
        var other = you switch { "inviter" => match.FriendUserId, "friend" => match.InviterUserId, _ => null };
        return new MatchViewDto(match.Token, StateOf(match), match.InviterDisplayName, match.FriendDisplayName, you, ResultOf(match), nameable,
            OtherUserId: match.Result is null || !tellOther ? null : other);
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
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Not yet", extensions: new Dictionary<string, object?> { ["code"] = code });
}
