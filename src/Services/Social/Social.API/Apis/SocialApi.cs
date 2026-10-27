using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Social.API.Data;
using TvTracker;

namespace Social.API.Apis;

/// <param name="FollowsYou">They follow the caller (approved).</param>
/// <param name="Requested">The caller asked to follow this private profile and is waiting for approval.</param>
/// <param name="BlockedByYou">The caller blocked them (only ever true on the caller's block list).</param>
public record PersonDto(string UserId, string DisplayName, int Followers, int Following, bool IsFollowing, bool FollowsYou, bool IsYou,
    bool Private = false, bool Requested = false, bool BlockedByYou = false);

/// <param name="Requests">Follow requests waiting for the caller's approval.</param>
public record MySocialDto(string UserId, string DisplayName, bool ShareActivity, int Followers, int Following, bool Private = false,
    int Requests = 0);

/// <summary>Either setting may be left out (null) to keep it as it is.</summary>
public record SocialSettings(bool? ShareActivity, bool? Private = null);

/// <param name="CanSee">False for a private profile the caller isn't an approved follower of: only the name shows.</param>
/// <param name="Activity">Their recent activity; empty when the caller can't see it.</param>
/// <param name="Shares">Whether the caller can see their activity.</param>
/// <param name="Lists">The lists they share.</param>
public record PersonPageDto(PersonDto Person, bool Shares, IReadOnlyList<FeedItemDto> Activity, bool CanSee = true,
    IReadOnlyList<SharedListSummaryDto>? Lists = null);

/// <param name="Following">The follow is in place (approved).</param>
/// <param name="Requested">It's a request to a private profile, waiting for approval.</param>
public record FollowStateDto(bool Following, bool Requested);

public record FeedDto(IReadOnlyList<FeedItemDto> Items, DateTime? NextBefore);

/// <param name="Kind">"watched", "finished", "followed" or "rated".</param>
/// <param name="Episodes">For "watched": how many episodes.</param>
/// <param name="First">For "watched": the earliest of them; null when the catalog copy doesn't know it yet.</param>
/// <param name="Last">For "watched": the latest of them, when more than one.</param>
public record FeedItemDto(Guid Id, string UserId, string DisplayName, string Kind, Guid ShowId, string? ShowTitle, string? PosterUrl,
    int Episodes, EpisodeCodeDto? First, EpisodeCodeDto? Last, int? Stars, DateTime OccurredAtUtc);

/// <summary>Numbers only: a follower may not have seen the episode, so titles stay out of the feed.</summary>
public record EpisodeCodeDto(int SeasonNumber, int EpisodeNumber);

public record SharedListSummaryDto(Guid ListId, string Name, string? Description, int Shows, DateTime UpdatedAtUtc);

public record SharedListDto(Guid ListId, PersonDto Owner, string Name, string? Description, IReadOnlyList<ListShowDto> Shows, DateTime UpdatedAtUtc);

public record ListShowDto(Guid ShowId, string? Title, string? PosterUrl);

/// <summary>
/// People following people: the feed, people pages, search, follow requests, blocks and shared lists. Who sees what is
/// decided by <see cref="Visibility"/>.
/// </summary>
public static class SocialApi
{
    public const int FeedPage = 30;
    private const int SearchLimit = 20;
    private const int PersonActivity = 20;

    public static RouteGroupBuilder MapSocialApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization().RequireFeature(FeatureFlags.Social);

        api.MapGet("/me/social", GetMeAsync);
        api.MapPut("/me/social/settings", UpdateSettingsAsync);
        api.MapGet("/me/follow-requests", GetRequestsAsync);
        api.MapPost("/me/follow-requests/{userId}/approve", ApproveAsync);
        api.MapDelete("/me/follow-requests/{userId}", DeclineAsync);
        api.MapGet("/me/blocks", GetBlocksAsync);
        api.MapGet("/feed", GetFeedAsync);
        api.MapGet("/people", SearchAsync);
        api.MapGet("/people/{userId}", GetPersonAsync);
        api.MapPost("/people/{userId}/follow", FollowAsync);
        api.MapDelete("/people/{userId}/follow", UnfollowAsync);
        api.MapPost("/people/{userId}/block", BlockAsync);
        api.MapDelete("/people/{userId}/block", UnblockAsync);
        api.MapGet("/people/{userId}/followers", GetFollowersAsync);
        api.MapGet("/people/{userId}/following", GetFollowingAsync);
        api.MapGet("/people/{userId}/lists/{listId:guid}", GetListAsync);

        // The data export works whether or not the feature is on: it's the user's data either way.
        app.MapGroup("/api/v1").RequireAuthorization().MapGet("/me/export", ExportAsync);

        return api;
    }

    private static async Task<Ok<MySocialDto>> GetMeAsync(ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var me = await EnsureProfileAsync(user, db, time, ct);
        return TypedResults.Ok(new MySocialDto(me.UserId, me.DisplayName, me.ShareActivity,
            await db.Follows.CountAsync(f => f.FolloweeId == me.UserId && f.Approved, ct),
            await db.Follows.CountAsync(f => f.FollowerId == me.UserId && f.Approved, ct),
            me.Private,
            await db.Follows.CountAsync(f => f.FolloweeId == me.UserId && !f.Approved, ct)));
    }

    /// <summary>Making a profile public approves every waiting request: nothing is left to approve.</summary>
    private static async Task<Ok<SocialSettings>> UpdateSettingsAsync(
        SocialSettings settings, ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var me = await EnsureProfileAsync(user, db, time, ct);
        me.ShareActivity = settings.ShareActivity ?? me.ShareActivity;
        if (settings.Private is { } isPrivate && isPrivate != me.Private)
        {
            me.Private = isPrivate;
            if (!isPrivate)
            {
                await db.Follows.Where(f => f.FolloweeId == me.UserId && !f.Approved)
                    .ExecuteUpdateAsync(f => f.SetProperty(x => x.Approved, true), ct);
            }
        }
        me.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new SocialSettings(me.ShareActivity, me.Private));
    }

    private static async Task<Ok<List<PersonDto>>> GetRequestsAsync(ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var me = UserId(user);
        var ids = await db.Follows.Where(f => f.FolloweeId == me && !f.Approved).OrderByDescending(f => f.CreatedAtUtc)
            .Select(f => f.FollowerId).ToListAsync(ct);
        return TypedResults.Ok(await PeopleAsync(db, me, ids, ct));
    }

    private static async Task<Results<NoContent, NotFound>> ApproveAsync(string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var updated = await db.Follows.Where(f => f.FollowerId == userId && f.FolloweeId == UserId(user) && !f.Approved)
            .ExecuteUpdateAsync(f => f.SetProperty(x => x.Approved, true), ct);
        return updated > 0 ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<NoContent> DeclineAsync(string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        await db.Follows.Where(f => f.FollowerId == userId && f.FolloweeId == UserId(user) && !f.Approved).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>The newest activity of the people the caller follows and can see; page backwards with <paramref name="before"/>.</summary>
    private static async Task<Ok<FeedDto>> GetFeedAsync(
        ClaimsPrincipal user, SocialContext db, DateTime? before = null, CancellationToken ct = default)
    {
        var visible = Visibility.VisibleFollowees(db, UserId(user));
        var cutoff = before ?? DateTime.MaxValue;
        var items = await db.Activities.AsNoTracking()
            .Where(a => visible.Contains(a.UserId) && a.OccurredAtUtc < cutoff)
            .OrderByDescending(a => a.OccurredAtUtc)
            .Take(FeedPage + 1)
            .ToListAsync(ct);

        var page = items.Take(FeedPage).ToList();
        return TypedResults.Ok(new FeedDto(await ToDtosAsync(db, page, ct), items.Count > FeedPage ? page[^1].OccurredAtUtc : null));
    }

    /// <summary>By display name. Private profiles are found (by name only); blocked people aren't, either way round.</summary>
    private static async Task<Results<Ok<List<PersonDto>>, ValidationProblem>> SearchAsync(
        ClaimsPrincipal user, SocialContext db, string q = "", CancellationToken ct = default)
    {
        var term = q.Trim();
        if (term.Length < 2)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Type at least 2 characters."] });
        }

        var me = UserId(user);
        var blocked = Visibility.BlockedWith(db, me);
        var pattern = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
        var ids = await db.Profiles
            .Where(p => p.UserId != me && !blocked.Contains(p.UserId) && EF.Functions.ILike(p.DisplayName, pattern))
            .OrderBy(p => p.DisplayName)
            .Select(p => p.UserId)
            .Take(SearchLimit)
            .ToListAsync(ct);
        return TypedResults.Ok(await PeopleAsync(db, me, ids, ct));
    }

    /// <summary>404 for someone blocked either way, as if they didn't exist.</summary>
    private static async Task<Results<Ok<PersonPageDto>, NotFound>> GetPersonAsync(
        string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var me = UserId(user);
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        var access = await Visibility.AccessAsync(db, me, profile, ct);
        if (!access.Exists)
        {
            return TypedResults.NotFound();
        }

        var person = (await PeopleAsync(db, me, [userId], ct)).Single();
        var activity = access.Activity
            ? await db.Activities.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.OccurredAtUtc).Take(PersonActivity).ToListAsync(ct)
            : [];
        var lists = access.Profile
            ? await db.SharedLists.AsNoTracking().Where(l => l.UserId == userId).OrderByDescending(l => l.UpdatedAtUtc)
                .Select(l => new SharedListSummaryDto(l.ListId, l.Name, l.Description, l.ShowIds.Count, l.UpdatedAtUtc)).ToListAsync(ct)
            : [];
        return TypedResults.Ok(new PersonPageDto(person, access.Activity, await ToDtosAsync(db, activity, ct), access.Profile, lists));
    }

    /// <summary>Following a private profile makes a request. Someone blocked either way can't follow (404, as if absent).</summary>
    private static async Task<Results<Ok<FollowStateDto>, NotFound, ValidationProblem>> FollowAsync(
        string userId, ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var me = UserId(user);
        if (userId == me)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["userId"] = ["You can't follow yourself."] });
        }

        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (!(await Visibility.AccessAsync(db, me, profile, ct)).Exists)
        {
            return TypedResults.NotFound();
        }

        await EnsureProfileAsync(user, db, time, ct);
        var follow = await db.Follows.FindAsync([me, userId], ct);
        if (follow is null)
        {
            follow = new UserFollow { FollowerId = me, FolloweeId = userId, CreatedAtUtc = time.GetUtcNow().UtcDateTime, Approved = !profile!.Private };
            db.Follows.Add(follow);
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.Ok(new FollowStateDto(follow.Approved, !follow.Approved));
    }

    /// <summary>Also withdraws a request that's still waiting.</summary>
    private static async Task<NoContent> UnfollowAsync(string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var me = UserId(user);
        await db.Follows.Where(f => f.FollowerId == me && f.FolloweeId == userId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Blocking ends any follow between the two, either way, and requests too.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> BlockAsync(
        string userId, ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var me = UserId(user);
        if (userId == me)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["userId"] = ["You can't block yourself."] });
        }
        if (!await db.Profiles.AnyAsync(p => p.UserId == userId, ct))
        {
            return TypedResults.NotFound();
        }

        await db.Follows.Where(f => (f.FollowerId == me && f.FolloweeId == userId) || (f.FollowerId == userId && f.FolloweeId == me))
            .ExecuteDeleteAsync(ct);
        if (await db.Blocks.FindAsync([me, userId], ct) is null)
        {
            db.Blocks.Add(new UserBlock { BlockerId = me, BlockedId = userId, CreatedAtUtc = time.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UnblockAsync(string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        await db.Blocks.Where(b => b.BlockerId == UserId(user) && b.BlockedId == userId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<PersonDto>>> GetBlocksAsync(ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var me = UserId(user);
        var ids = await db.Blocks.Where(b => b.BlockerId == me).OrderByDescending(b => b.CreatedAtUtc).Select(b => b.BlockedId).ToListAsync(ct);
        return TypedResults.Ok(await PeopleAsync(db, me, ids, ct, includeBlocked: true));
    }

    private static Task<Results<Ok<List<PersonDto>>, NotFound>> GetFollowersAsync(string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct) =>
        ConnectionsAsync(userId, user, db, followers: true, ct);

    private static Task<Results<Ok<List<PersonDto>>, NotFound>> GetFollowingAsync(string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct) =>
        ConnectionsAsync(userId, user, db, followers: false, ct);

    /// <summary>Approved follows only. A private profile's connections are for its approved followers.</summary>
    private static async Task<Results<Ok<List<PersonDto>>, NotFound>> ConnectionsAsync(
        string userId, ClaimsPrincipal user, SocialContext db, bool followers, CancellationToken ct)
    {
        var me = UserId(user);
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        var access = await Visibility.AccessAsync(db, me, profile, ct);
        if (!access.Exists)
        {
            return TypedResults.NotFound();
        }
        if (!access.Profile)
        {
            return TypedResults.Ok(new List<PersonDto>());
        }

        var follows = db.Follows.Where(f => f.Approved);
        var ids = followers
            ? await follows.Where(f => f.FolloweeId == userId).OrderByDescending(f => f.CreatedAtUtc).Select(f => f.FollowerId).ToListAsync(ct)
            : await follows.Where(f => f.FollowerId == userId).OrderByDescending(f => f.CreatedAtUtc).Select(f => f.FolloweeId).ToListAsync(ct);
        return TypedResults.Ok(await PeopleAsync(db, me, ids, ct));
    }

    private static async Task<Results<Ok<SharedListDto>, NotFound>> GetListAsync(
        string userId, Guid listId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var me = UserId(user);
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        var list = await db.SharedLists.AsNoTracking().SingleOrDefaultAsync(l => l.ListId == listId && l.UserId == userId, ct);
        if (list is null || !(await Visibility.AccessAsync(db, me, profile, ct)).Profile)
        {
            return TypedResults.NotFound();
        }

        var shows = await db.Shows.AsNoTracking().Where(s => list.ShowIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        var owner = (await PeopleAsync(db, me, [userId], ct)).Single();
        return TypedResults.Ok(new SharedListDto(list.ListId, owner, list.Name, list.Description,
            list.ShowIds.Select(id => new ListShowDto(id, shows.GetValueOrDefault(id)?.Title, shows.GetValueOrDefault(id)?.PosterUrl)).ToList(),
            list.UpdatedAtUtc));
    }

    /// <summary>For Identity's data export: the caller's settings, follows, requests, blocks and activity.</summary>
    private static async Task<Ok<object>> ExportAsync(ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var me = UserId(user);
        return TypedResults.Ok<object>(new
        {
            Profile = await db.Profiles.AsNoTracking().Where(p => p.UserId == me).Select(p => new { p.DisplayName, p.ShareActivity, p.Private }).SingleOrDefaultAsync(ct),
            Following = await db.Follows.AsNoTracking().Where(f => f.FollowerId == me).Select(f => new { f.FolloweeId, f.Approved, f.CreatedAtUtc }).ToListAsync(ct),
            Followers = await db.Follows.AsNoTracking().Where(f => f.FolloweeId == me).Select(f => new { f.FollowerId, f.Approved, f.CreatedAtUtc }).ToListAsync(ct),
            Blocked = await db.Blocks.AsNoTracking().Where(b => b.BlockerId == me).Select(b => new { b.BlockedId, b.CreatedAtUtc }).ToListAsync(ct),
            Activity = await db.Activities.AsNoTracking().Where(a => a.UserId == me).OrderBy(a => a.OccurredAtUtc)
                .Select(a => new { a.Kind, a.ShowId, a.EpisodeIds, a.Stars, a.OccurredAtUtc }).ToListAsync(ct)
        });
    }

    /// <summary>People in the order of <paramref name="ids"/>, with follow counts and how they relate to the caller.</summary>
    /// <param name="includeBlocked">Keep people blocked either way (the caller's own block list); others drop them.</param>
    internal static async Task<List<PersonDto>> PeopleAsync(SocialContext db, string me, List<string> ids, CancellationToken ct, bool includeBlocked = false)
    {
        var blocked = (await Visibility.BlockedWith(db, me).Where(id => ids.Contains(id)).ToListAsync(ct)).ToHashSet();
        var blockedByMe = (await db.Blocks.Where(b => b.BlockerId == me && ids.Contains(b.BlockedId)).Select(b => b.BlockedId).ToListAsync(ct)).ToHashSet();
        var profiles = await db.Profiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, ct);
        var approved = db.Follows.Where(f => f.Approved);
        var followers = await approved.Where(f => ids.Contains(f.FolloweeId)).GroupBy(f => f.FolloweeId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var following = await approved.Where(f => ids.Contains(f.FollowerId)).GroupBy(f => f.FollowerId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var mine = await db.Follows.Where(f => f.FollowerId == me && ids.Contains(f.FolloweeId)).ToDictionaryAsync(f => f.FolloweeId, f => f.Approved, ct);
        var followMe = (await approved.Where(f => f.FolloweeId == me && ids.Contains(f.FollowerId)).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();

        return ids.Where(id => profiles.ContainsKey(id) && (includeBlocked || !blocked.Contains(id)))
            .Select(id => new PersonDto(id, profiles[id].DisplayName, followers.GetValueOrDefault(id), following.GetValueOrDefault(id),
                mine.TryGetValue(id, out var ok) && ok, followMe.Contains(id), id == me,
                profiles[id].Private, mine.TryGetValue(id, out var pending) && !pending, blockedByMe.Contains(id)))
            .ToList();
    }

    private static async Task<List<FeedItemDto>> ToDtosAsync(SocialContext db, List<Activity> items, CancellationToken ct)
    {
        var userIds = items.Select(a => a.UserId).Distinct().ToList();
        var showIds = items.Select(a => a.ShowId).Distinct().ToList();
        var episodeIds = items.SelectMany(a => a.EpisodeIds).Distinct().ToList();
        var names = await db.Profiles.AsNoTracking().Where(p => userIds.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.DisplayName, ct);
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        var episodes = await db.Episodes.AsNoTracking().Where(e => episodeIds.Contains(e.EpisodeId)).ToDictionaryAsync(e => e.EpisodeId, ct);

        return items.Select(a =>
            {
                var known = a.EpisodeIds.Where(episodes.ContainsKey).Select(id => episodes[id])
                    .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
                var show = shows.GetValueOrDefault(a.ShowId);
                return new FeedItemDto(a.Id, a.UserId, names.GetValueOrDefault(a.UserId) ?? "Someone", a.Kind, a.ShowId, show?.Title, show?.PosterUrl,
                    a.EpisodeIds.Count,
                    known.Count > 0 ? new EpisodeCodeDto(known[0].SeasonNumber, known[0].EpisodeNumber) : null,
                    known.Count > 1 ? new EpisodeCodeDto(known[^1].SeasonNumber, known[^1].EpisodeNumber) : null,
                    a.Stars, a.OccurredAtUtc);
            })
            .ToList();
    }

    internal static string UserId(ClaimsPrincipal user) => user.FindFirst("sub")?.Value ?? throw new InvalidOperationException("No sub claim.");

    /// <summary>
    /// The caller's profile. Identity sends names for everyone, but a caller can arrive here first; their token's name
    /// stands in until Identity's event or snapshot arrives.
    /// </summary>
    private static async Task<Profile> EnsureProfileAsync(ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var id = UserId(user);
        if (await db.Profiles.FindAsync([id], ct) is { } profile)
        {
            return profile;
        }

        profile = new Profile
        {
            UserId = id,
            DisplayName = user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value ?? "TV Tracker user",
            UpdatedAtUtc = time.GetUtcNow().UtcDateTime
        };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }
}
