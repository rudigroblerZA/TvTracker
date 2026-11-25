using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// The waiting room (see <see cref="WaitingRoom"/>), computed on request from the caller's own watches, the catalog copy and
/// the hourly show documents.
/// </summary>
public static class WaitingRoomApi
{
    public static void MapWaitingRoomApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/waiting", GetAsync).RequireFeature(FeatureFlags.WaitingRoom);

    private static async Task<Ok<WaitingRoomDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(new WaitingRoomDto((await ComputeAsync(db, user.GetUserId(), time.GetUtcNow(), ct)).Shows));

    /// <param name="Shows">Soonest back first.</param>
    /// <param name="Copies">The caught-up shows' catalog copies (genres).</param>
    /// <param name="Documents">Their insights documents (DNA), loaded for the forecasts.</param>
    /// <param name="Started">Every show the caller has a watch on, from the progress aggregate.</param>
    internal sealed record Room(List<WaitingShowDto> Shows, Dictionary<Guid, ShowCopy> Copies, Dictionary<Guid, ShowInsights> Documents,
        HashSet<Guid> Started);

    /// <summary>
    /// The caller's waiting room, with what it loaded on the way, so While you wait reads nothing twice.
    /// </summary>
    internal static async Task<Room> ComputeAsync(AnalyticsContext db, string userId, DateTimeOffset now, CancellationToken ct)
    {
        // Caught up: every aired regular episode watched (the aggregate), on a show that's still running.
        var progress = await Viewer.ProgressAsync(db, userId, now, null, ct);
        var allWatched = progress.Values.Where(p => p.AllWatched).Select(p => p.ShowId).ToList();
        var shows = await db.Shows.AsNoTracking()
            .Running().Where(s => allWatched.Contains(s.ShowId))
            .ToDictionaryAsync(s => s.ShowId, ct);
        var caughtUp = shows.Keys.ToList();
        var upcoming = (await db.Episodes.AsNoTracking()
                .Where(e => caughtUp.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc > now)
                .Select(e => new { e.ShowId, e.SeasonNumber, e.AirsAtUtc })
                .ToListAsync(ct))
            .ToLookup(e => e.ShowId);
        var documents = await ShowDocuments.LoadAsync(db, caughtUp, ct);

        var result = WaitingRoom.Ordered(caughtUp.Select(id =>
        {
            var next = upcoming[id].MinBy(e => e.AirsAtUtc);
            return WaitingRoom.Of(shows[id], documents.GetValueOrDefault(id), progress[id].LatestSeason, progress[id].LastAiredAtUtc,
                next is null ? null : (next.SeasonNumber, next.AirsAtUtc));
        }));

        return new Room(result, shows, documents, progress.Keys.ToHashSet());
    }
}
