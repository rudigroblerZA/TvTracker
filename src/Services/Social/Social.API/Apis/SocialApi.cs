using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Social.API.Data;
using TvTracker;

namespace Social.API.Apis;

/// <param name="FollowsYou">They follow the caller.</param>
public record PersonDto(string UserId, string DisplayName, int Followers, int Following, bool IsFollowing, bool FollowsYou, bool IsYou);

public record MySocialDto(string UserId, string DisplayName, bool ShareActivity, int Followers, int Following);

public record SocialSettings(bool ShareActivity);

/// <param name="Activity">Their recent activity; empty when they don't share it.</param>
/// <param name="Shares">Whether they share their activity (always true for the caller's own page).</param>
public record PersonPageDto(PersonDto Person, bool Shares, IReadOnlyList<FeedItemDto> Activity);

public record FeedDto(IReadOnlyList<FeedItemDto> Items, DateTime? NextBefore);

/// <param name="Kind">"watched", "finished", "followed" or "rated".</param>
/// <param name="Episodes">For "watched": how many episodes.</param>
/// <param name="First">For "watched": the earliest of them; null when the catalog copy doesn't know it yet.</param>
/// <param name="Last">For "watched": the latest of them, when more than one.</param>
public record FeedItemDto(Guid Id, string UserId, string DisplayName, string Kind, Guid ShowId, string? ShowTitle, string? PosterUrl,
    int Episodes, EpisodeCodeDto? First, EpisodeCodeDto? Last, int? Stars, DateTime OccurredAtUtc);

/// <summary>Numbers only: a follower may not have seen the episode, so titles stay out of the feed.</summary>
public record EpisodeCodeDto(int SeasonNumber, int EpisodeNumber);

/// <summary>
/// People following people, and the feed of what the people you follow watch and rate. Following is one way and needs
/// no approval; what a follower sees is controlled by the followed user's "share my activity" setting, which hides
/// all of their activity (past and future) at once.
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
        api.MapGet("/feed", GetFeedAsync);
        api.MapGet("/people", SearchAsync);
        api.MapGet("/people/{userId}", GetPersonAsync);
        api.MapPost("/people/{userId}/follow", FollowAsync);
        api.MapDelete("/people/{userId}/follow", UnfollowAsync);
        api.MapGet("/people/{userId}/followers", GetFollowersAsync);
        api.MapGet("/people/{userId}/following", GetFollowingAsync);

        // The data export works whether or not the feature is on: it's the user's data either way.
        app.MapGroup("/api/v1").RequireAuthorization().MapGet("/me/export", ExportAsync);

        return api;
    }

    private static async Task<Ok<MySocialDto>> GetMeAsync(ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var me = await EnsureProfileAsync(user, db, time, ct);
        return TypedResults.Ok(new MySocialDto(me.UserId, me.DisplayName, me.ShareActivity,
            await db.Follows.CountAsync(f => f.FolloweeId == me.UserId, ct),
            await db.Follows.CountAsync(f => f.FollowerId == me.UserId, ct)));
    }

    private static async Task<Ok<SocialSettings>> UpdateSettingsAsync(
        SocialSettings settings, ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var me = await EnsureProfileAsync(user, db, time, ct);
        me.ShareActivity = settings.ShareActivity;
        me.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new SocialSettings(me.ShareActivity));
    }

    /// <summary>The newest activity of the people the caller follows who share it; page backwards with <paramref name="before"/>.</summary>
    private static async Task<Ok<FeedDto>> GetFeedAsync(
        ClaimsPrincipal user, SocialContext db, DateTime? before = null, CancellationToken ct = default)
    {
        var me = UserId(user);
        var followed = db.Follows.Where(f => f.FollowerId == me).Select(f => f.FolloweeId);
        var sharing = db.Profiles.Where(p => p.ShareActivity && followed.Contains(p.UserId)).Select(p => p.UserId);
        var cutoff = before ?? DateTime.MaxValue;
        var items = await db.Activities.AsNoTracking()
            .Where(a => sharing.Contains(a.UserId) && a.OccurredAtUtc < cutoff)
            .OrderByDescending(a => a.OccurredAtUtc)
            .Take(FeedPage + 1)
            .ToListAsync(ct);

        var page = items.Take(FeedPage).ToList();
        return TypedResults.Ok(new FeedDto(await ToDtosAsync(db, page, ct), items.Count > FeedPage ? page[^1].OccurredAtUtc : null));
    }

    private static async Task<Results<Ok<List<PersonDto>>, ValidationProblem>> SearchAsync(
        ClaimsPrincipal user, SocialContext db, string q = "", CancellationToken ct = default)
    {
        var term = q.Trim();
        if (term.Length < 2)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Type at least 2 characters."] });
        }

        var me = UserId(user);
        var pattern = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
        var ids = await db.Profiles
            .Where(p => p.UserId != me && EF.Functions.ILike(p.DisplayName, pattern))
            .OrderBy(p => p.DisplayName)
            .Select(p => p.UserId)
            .Take(SearchLimit)
            .ToListAsync(ct);
        return TypedResults.Ok(await PeopleAsync(db, me, ids, ct));
    }

    private static async Task<Results<Ok<PersonPageDto>, NotFound>> GetPersonAsync(
        string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var me = UserId(user);
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null)
        {
            return TypedResults.NotFound();
        }

        var person = (await PeopleAsync(db, me, [userId], ct)).Single();
        var shares = profile.ShareActivity || userId == me;
        var activity = shares
            ? await db.Activities.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.OccurredAtUtc).Take(PersonActivity).ToListAsync(ct)
            : [];
        return TypedResults.Ok(new PersonPageDto(person, shares, await ToDtosAsync(db, activity, ct)));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> FollowAsync(
        string userId, ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var me = UserId(user);
        if (userId == me)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["userId"] = ["You can't follow yourself."] });
        }
        if (!await db.Profiles.AnyAsync(p => p.UserId == userId, ct))
        {
            return TypedResults.NotFound();
        }

        await EnsureProfileAsync(user, db, time, ct);
        if (await db.Follows.FindAsync([me, userId], ct) is null)
        {
            db.Follows.Add(new UserFollow { FollowerId = me, FolloweeId = userId, CreatedAtUtc = time.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UnfollowAsync(string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var me = UserId(user);
        await db.Follows.Where(f => f.FollowerId == me && f.FolloweeId == userId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<PersonDto>>> GetFollowersAsync(string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var ids = await db.Follows.Where(f => f.FolloweeId == userId).OrderByDescending(f => f.CreatedAtUtc).Select(f => f.FollowerId).ToListAsync(ct);
        return TypedResults.Ok(await PeopleAsync(db, UserId(user), ids, ct));
    }

    private static async Task<Ok<List<PersonDto>>> GetFollowingAsync(string userId, ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var ids = await db.Follows.Where(f => f.FollowerId == userId).OrderByDescending(f => f.CreatedAtUtc).Select(f => f.FolloweeId).ToListAsync(ct);
        return TypedResults.Ok(await PeopleAsync(db, UserId(user), ids, ct));
    }

    /// <summary>For Identity's data export: the caller's settings, who they follow, their followers and their activity.</summary>
    private static async Task<Ok<object>> ExportAsync(ClaimsPrincipal user, SocialContext db, CancellationToken ct)
    {
        var me = UserId(user);
        return TypedResults.Ok<object>(new
        {
            Profile = await db.Profiles.AsNoTracking().Where(p => p.UserId == me).Select(p => new { p.DisplayName, p.ShareActivity }).SingleOrDefaultAsync(ct),
            Following = await db.Follows.AsNoTracking().Where(f => f.FollowerId == me).Select(f => new { f.FolloweeId, f.CreatedAtUtc }).ToListAsync(ct),
            Followers = await db.Follows.AsNoTracking().Where(f => f.FolloweeId == me).Select(f => new { f.FollowerId, f.CreatedAtUtc }).ToListAsync(ct),
            Activity = await db.Activities.AsNoTracking().Where(a => a.UserId == me).OrderBy(a => a.OccurredAtUtc)
                .Select(a => new { a.Kind, a.ShowId, a.EpisodeIds, a.Stars, a.OccurredAtUtc }).ToListAsync(ct)
        });
    }

    /// <summary>People in the order of <paramref name="ids"/>, with follow counts and how they relate to the caller.</summary>
    private static async Task<List<PersonDto>> PeopleAsync(SocialContext db, string me, List<string> ids, CancellationToken ct)
    {
        var profiles = await db.Profiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, ct);
        var followers = await db.Follows.Where(f => ids.Contains(f.FolloweeId)).GroupBy(f => f.FolloweeId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var following = await db.Follows.Where(f => ids.Contains(f.FollowerId)).GroupBy(f => f.FollowerId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var iFollow = (await db.Follows.Where(f => f.FollowerId == me && ids.Contains(f.FolloweeId)).Select(f => f.FolloweeId).ToListAsync(ct)).ToHashSet();
        var followMe = (await db.Follows.Where(f => f.FolloweeId == me && ids.Contains(f.FollowerId)).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();

        return ids.Where(profiles.ContainsKey)
            .Select(id => new PersonDto(id, profiles[id].DisplayName, followers.GetValueOrDefault(id), following.GetValueOrDefault(id),
                iFollow.Contains(id), followMe.Contains(id), id == me))
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

    private static string UserId(ClaimsPrincipal user) => user.FindFirst("sub")?.Value ?? throw new InvalidOperationException("No sub claim.");

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
