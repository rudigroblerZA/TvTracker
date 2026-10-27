using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Social.API.Data;
using TvTracker;

namespace Social.API.Apis;

/// <param name="SeenBy">"you" or "them" when one of the two has already watched it (a rewatch for them); null when it's
/// new to both.</param>
/// <param name="RuntimeEstimated">The provider has no runtime; it's the show's typical one.</param>
public record TogetherEpisodeDto(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, int RuntimeMinutes, bool RuntimeEstimated, string? SeenBy);

/// <param name="Ahead">"you", "them", "level" (same place), or null when neither has started.</param>
/// <param name="Gap">How far apart the two are, in episodes: the rewatches the one ahead faces.</param>
/// <param name="LastWatchedAtUtc">The later of the two's last watch of the show.</param>
public record TonightTogetherShowDto(Guid ShowId, string Title, string? PosterUrl, string? Ahead, int Gap,
    DateTime? LastWatchedAtUtc, IReadOnlyList<TogetherEpisodeDto> Episodes);

/// <summary>A shared show left out because one of the two is too far ahead to catch up in an evening.</summary>
public record ApartShowDto(Guid ShowId, string Title, string Ahead, int Gap);

public record TonightTogetherDto(PersonDto Person, IReadOnlyList<TonightTogetherShowDto> Shows, IReadOnlyList<ApartShowDto> Apart);

/// <summary>
/// Tonight for two: the queue for an evening watched together, from Social's copy of both people's watches. Planning
/// stays in the UI's TonightPlanner, as for one; this only says what the two of them could watch next.
/// <list type="bullet">
/// <item><b>Shows:</b> ones both follow or have started, as in watching together, and not finished by both.</item>
/// <item>
///   <b>Where to start:</b> after the furthest episode of whoever is behind, so nobody skips anything. Episodes the
///   one ahead has seen are rewatches for them (<c>SeenBy</c>); the planner values them lower.
/// </item>
/// <item>
///   <b>Too far apart:</b> a gap over <see cref="MaxGap"/> episodes would make the evening a catch-up for one of them,
///   so the show is listed apart instead.
/// </item>
/// <item><b>No titles:</b> Social keeps none (a follower may not have seen an episode), so episodes are numbers.</item>
/// </list>
/// Seen only under <see cref="Visibility"/>'s activity rule, like watching together; 404 otherwise.
/// </summary>
public static class TonightTogetherApi
{
    public const int MaxGap = 3;
    private const int DefaultPerShow = 8;
    private const int MaxPerShow = 20;
    private const int FallbackRuntime = 45;

    public static void MapTonightTogetherApi(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1").RequireAuthorization()
            .RequireFeature(FeatureFlags.Social).RequireFeature(FeatureFlags.TonightTogether)
            .MapGet("/people/{userId}/tonight", GetAsync);
    }

    private static async Task<Results<Ok<TonightTogetherDto>, NotFound>> GetAsync(
        string userId, int? perShow, ClaimsPrincipal user, SocialContext db, TimeProvider time, CancellationToken ct)
    {
        var me = SocialApi.UserId(user);
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (userId == me || !(await Visibility.AccessAsync(db, me, profile, ct)).Activity)
        {
            return TypedResults.NotFound();
        }

        var take = Math.Clamp(perShow ?? DefaultPerShow, 1, MaxPerShow);
        var shared = (await TogetherApi.ShowsOfAsync(db, me, ct)).Intersect(await TogetherApi.ShowsOfAsync(db, userId, ct)).ToList();
        var now = time.GetUtcNow();
        var episodes = (await db.Episodes.AsNoTracking()
                .Where(e => shared.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
                .ToListAsync(ct))
            .ToLookup(e => e.ShowId);
        var watches = (await db.Watches.AsNoTracking()
                .Where(w => (w.UserId == me || w.UserId == userId) && shared.Contains(w.ShowId))
                .Select(w => new { w.UserId, w.ShowId, w.EpisodeId, w.WatchedAtUtc })
                .ToListAsync(ct))
            .ToLookup(w => w.ShowId);
        var shows = await db.Shows.AsNoTracking().Where(s => shared.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        var result = new List<TonightTogetherShowDto>();
        var apart = new List<ApartShowDto>();
        foreach (var showId in shared)
        {
            if (!shows.TryGetValue(showId, out var show))
            {
                continue;
            }

            var order = episodes[showId].OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ThenBy(e => e.AirsAtUtc).ToList();
            var mine = watches[showId].Where(w => w.UserId == me).Select(w => w.EpisodeId).ToHashSet();
            var theirs = watches[showId].Where(w => w.UserId == userId).Select(w => w.EpisodeId).ToHashSet();
            var myFurthest = order.FindLastIndex(e => mine.Contains(e.EpisodeId));
            var theirFurthest = order.FindLastIndex(e => theirs.Contains(e.EpisodeId));
            var gap = Math.Abs(myFurthest - theirFurthest);
            var ahead = myFurthest < 0 && theirFurthest < 0 ? null : myFurthest > theirFurthest ? "you" : myFurthest < theirFurthest ? "them" : "level";

            // Both seen everything aired: nothing to watch together until more airs.
            var start = Math.Min(myFurthest, theirFurthest) + 1;
            if (start >= order.Count || order.Skip(start).All(e => mine.Contains(e.EpisodeId) && theirs.Contains(e.EpisodeId)))
            {
                continue;
            }
            if (gap > MaxGap)
            {
                apart.Add(new ApartShowDto(showId, show.Title, ahead!, gap));
                continue;
            }

            var runtimes = order.Where(e => e.RuntimeMinutes > 0).Select(e => e.RuntimeMinutes!.Value).Order().ToList();
            var typical = runtimes.Count > 0 ? runtimes[runtimes.Count / 2] : FallbackRuntime;
            var queue = order.Skip(start)
                .Where(e => !(mine.Contains(e.EpisodeId) && theirs.Contains(e.EpisodeId)))
                .Take(take)
                .Select(e => new TogetherEpisodeDto(e.EpisodeId, e.SeasonNumber, e.EpisodeNumber,
                    e.RuntimeMinutes is > 0 ? e.RuntimeMinutes.Value : typical, e.RuntimeMinutes is not > 0,
                    mine.Contains(e.EpisodeId) ? "you" : theirs.Contains(e.EpisodeId) ? "them" : null))
                .ToList();
            var last = watches[showId].Select(w => (DateTime?)w.WatchedAtUtc).Max();
            result.Add(new TonightTogetherShowDto(showId, show.Title, show.PosterUrl, ahead, gap, last, queue));
        }

        var person = (await SocialApi.PeopleAsync(db, me, [userId], ct)).Single();
        return TypedResults.Ok(new TonightTogetherDto(person, result, apart.OrderBy(a => a.Gap).ToList()));
    }
}
