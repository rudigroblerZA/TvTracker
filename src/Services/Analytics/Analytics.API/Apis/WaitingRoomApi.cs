using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// The waiting room: running shows the caller is caught up on (every aired regular episode watched), with when each is
/// back. Computed on request from the caller's own watches and the catalog copy, plus the hourly show documents'
/// <see cref="ShowInsights.NextPremiere"/> and <see cref="Outlook"/>; ended shows have nothing to wait for.
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

        var result = caughtUp.Select(id =>
            {
                var show = shows[id];
                var insights = documents.GetValueOrDefault(id);
                var lastAired = progress[id].LastAiredAtUtc;
                var next = upcoming[id].MinBy(e => e.AirsAtUtc);
                var currentSeason = progress[id].LatestSeason;

                if (next is not null && next.SeasonNumber == currentSeason)
                {
                    return new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "episode", next.AirsAtUtc, next.SeasonNumber,
                        null, null, null, false, null, lastAired);
                }
                if (insights?.NextPremiere is { } premiere)
                {
                    return new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "season", premiere.PremieresAtUtc,
                        premiere.SeasonNumber, null, null, null, false, null, lastAired);
                }
                if (next is not null)
                {
                    return new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "season", next.AirsAtUtc, next.SeasonNumber,
                        null, null, null, false, null, lastAired);
                }
                var renewal = insights?.Outlook?.Renewal?.Share;
                return insights?.Outlook?.NextSeason is { } forecast
                    ? new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "forecast", null, forecast.SeasonNumber,
                        forecast.Likely, forecast.Earliest, forecast.Latest, forecast.Overdue, renewal, lastAired)
                    : new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "unknown", null, null, null, null, null, false,
                        renewal, lastAired);
            })
            .OrderBy(s => s.Kind is "episode" or "season" ? 0 : s.Kind == "forecast" ? 1 : 2)
            .ThenBy(s => s.BackAtUtc ?? (s.Likely is { } likely ? new DateTimeOffset(likely.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : DateTimeOffset.MaxValue))
            .ThenByDescending(s => s.LastAiredUtc)
            .ToList();

        return new Room(result, shows, documents, progress.Keys.ToHashSet());
    }
}
