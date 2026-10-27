using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Social.API.Data;
using TvTracker;

namespace Social.API.Apis;

/// <summary>How far someone is through a show: episodes watched of those aired, and the furthest one.</summary>
/// <param name="Finished">Every aired regular episode watched.</param>
public record ProgressDto(int Watched, int Aired, EpisodeCodeDto? Furthest, bool Finished);

/// <param name="Stars">Their rating of the show, 1-5.</param>
public record FriendOnShowDto(string UserId, string DisplayName, bool Following, ProgressDto Progress, int? Stars);

/// <param name="Total">Everyone the caller follows and can see who follows, watched or rated the show.</param>
public record FriendsOnShowDto(int Total, IReadOnlyList<FriendOnShowDto> People);

/// <param name="Ahead">"you", "them", "level", or null when neither has started.</param>
public record TogetherShowDto(Guid ShowId, string? Title, string? PosterUrl, ProgressDto You, ProgressDto Them, string? Ahead);

public record TogetherDto(PersonDto Person, IReadOnlyList<TogetherShowDto> Shows);

/// <summary>
/// The social side of a show: who you follow follows, watches or rated it, and how far they are; and, with one person,
/// the shows you share. Built from Social's copy of each user's follows, watches and ratings, and seen only under
/// <see cref="Visibility"/>'s activity rule. Progress counts regular episodes (season 0 left out, as Tracking does).
/// </summary>
public static class TogetherApi
{
    private const int MaxPeople = 20;
    private const int MaxShows = 30;

    public static void MapTogetherApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization().RequireFeature(FeatureFlags.Social);
        api.MapGet("/shows/{showId:guid}/friends", GetFriendsAsync);
        api.MapGet("/people/{userId}/together", GetTogetherAsync);
    }

    /// <summary>People the caller follows and can see, furthest along first, then raters.</summary>
    private static async Task<Ok<FriendsOnShowDto>> GetFriendsAsync(Guid showId, ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var visible = Visibility.VisibleFollowees(db, SocialApi.UserId(user));
        var shows = await db.UserShows.AsNoTracking()
            .Where(s => s.ShowId == showId && visible.Contains(s.UserId) && (s.Followed || s.Stars != null))
            .ToDictionaryAsync(s => s.UserId, ct);
        var watchers = await db.Watches.AsNoTracking().Where(w => w.ShowId == showId && visible.Contains(w.UserId))
            .Select(w => w.UserId).Distinct().ToListAsync(ct);
        var ids = shows.Keys.Union(watchers).ToList();
        var progress = await ProgressAsync(db, ids, [showId], time.GetUtcNow(), ct);
        var names = await db.Profiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.DisplayName, ct);

        var people = ids.Where(names.ContainsKey)
            .Select(id => new FriendOnShowDto(id, names[id], shows.GetValueOrDefault(id)?.Followed == true,
                progress[(id, showId)], shows.GetValueOrDefault(id)?.Stars))
            .OrderByDescending(p => p.Progress.Finished)
            .ThenByDescending(p => p.Progress.Watched)
            .ThenByDescending(p => p.Stars)
            .ToList();
        return TypedResults.Ok(new FriendsOnShowDto(people.Count, people.Take(MaxPeople).ToList()));
    }

    /// <summary>
    /// Shows both follow or have started: both part-way first (the ones to talk about), then ones only one has started,
    /// then ones neither has. 404 when the caller can't see the other's activity.
    /// </summary>
    private static async Task<Results<Ok<TogetherDto>, NotFound>> GetTogetherAsync(
        string userId, ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var me = SocialApi.UserId(user);
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (userId == me || !(await Visibility.AccessAsync(db, me, profile, ct)).Activity)
        {
            return TypedResults.NotFound();
        }

        var mine = await ShowsOfAsync(db, me, ct);
        var theirs = await ShowsOfAsync(db, userId, ct);
        var shared = mine.Intersect(theirs).ToList();
        var progress = await ProgressAsync(db, [me, userId], shared, time.GetUtcNow(), ct);
        var shows = await db.Shows.AsNoTracking().Where(s => shared.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        var together = shared
            .Select(id =>
            {
                var you = progress[(me, id)];
                var them = progress[(userId, id)];
                return new TogetherShowDto(id, shows.GetValueOrDefault(id)?.Title, shows.GetValueOrDefault(id)?.PosterUrl, you, them, Ahead(you, them));
            })
            .OrderBy(s => (s.You.Watched > 0 && !s.You.Finished ? 0 : 1) + (s.Them.Watched > 0 && !s.Them.Finished ? 0 : 1))
            .ThenByDescending(s => s.You.Watched + s.Them.Watched)
            .Take(MaxShows)
            .ToList();
        var person = (await SocialApi.PeopleAsync(db, me, [userId], ct)).Single();
        return TypedResults.Ok(new TogetherDto(person, together));
    }

    /// <summary>Shows a user follows or has watched any of.</summary>
    internal static async Task<HashSet<Guid>> ShowsOfAsync(SocialContext db, string userId, CancellationToken ct) =>
        (await db.UserShows.Where(s => s.UserId == userId && s.Followed).Select(s => s.ShowId)
            .Union(db.Watches.Where(w => w.UserId == userId).Select(w => w.ShowId))
            .ToListAsync(ct)).ToHashSet();

    private static string? Ahead(ProgressDto you, ProgressDto them)
    {
        if (you.Furthest is null && them.Furthest is null)
        {
            return null;
        }
        var order = Comparer<(int, int)>.Default.Compare(
            you.Furthest is { } y ? (y.SeasonNumber, y.EpisodeNumber) : (0, 0),
            them.Furthest is { } t ? (t.SeasonNumber, t.EpisodeNumber) : (0, 0));
        return order > 0 ? "you" : order < 0 ? "them" : "level";
    }

    /// <summary>Each user's progress through each show, keyed by (user, show), from their watches and the episode copy.</summary>
    private static async Task<Dictionary<(string, Guid), ProgressDto>> ProgressAsync(
        SocialContext db, List<string> userIds, List<Guid> showIds, DateTimeOffset now, CancellationToken ct)
    {
        var aired = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ShowId, g => g.Count, ct);
        var watched = await db.Watches.AsNoTracking()
            .Where(w => userIds.Contains(w.UserId) && showIds.Contains(w.ShowId))
            .Join(db.Episodes.Where(e => e.SeasonNumber > 0), w => w.EpisodeId, e => e.EpisodeId,
                (w, e) => new { w.UserId, w.ShowId, e.SeasonNumber, e.EpisodeNumber })
            .ToListAsync(ct);
        var byUserShow = watched.ToLookup(w => (w.UserId, w.ShowId));

        var result = new Dictionary<(string, Guid), ProgressDto>();
        foreach (var userId in userIds)
        {
            foreach (var showId in showIds)
            {
                var mine = byUserShow[(userId, showId)].ToList();
                var furthest = mine.MaxBy(w => (w.SeasonNumber, w.EpisodeNumber));
                var total = aired.GetValueOrDefault(showId);
                result[(userId, showId)] = new ProgressDto(mine.Count, total,
                    furthest is null ? null : new EpisodeCodeDto(furthest.SeasonNumber, furthest.EpisodeNumber),
                    total > 0 && mine.Count >= total);
            }
        }
        return result;
    }
}
