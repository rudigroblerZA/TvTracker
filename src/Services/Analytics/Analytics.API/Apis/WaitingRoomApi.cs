using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <param name="Shows">Soonest back first: dated returns, then forecasts by their likely date, then the unknown.</param>
public record WaitingRoomDto(IReadOnlyList<WaitingShowDto> Shows);

/// <param name="Kind"><c>episode</c> (mid-season, next episode dated), <c>season</c> (next season dated),
/// <c>forecast</c> (from the show's own waits), or <c>unknown</c>.</param>
/// <param name="BackAtUtc">The dated episode or premiere; null for forecasts and the unknown.</param>
/// <param name="SeasonNumber">The season it's back with, when known.</param>
/// <param name="Likely">For a forecast, with <paramref name="Earliest"/> and <paramref name="Latest"/> (the 80% range).</param>
/// <param name="Overdue">A forecast already past its range.</param>
/// <param name="Renewal">For an undated return, how often comparable shows came back (a weak signal; 0-1).</param>
/// <param name="LastAiredUtc">Its latest aired episode, which they've watched.</param>
public record WaitingShowDto(Guid ShowId, string Title, string? PosterUrl, string? Network, string Kind, DateTimeOffset? BackAtUtc,
    int? SeasonNumber, DateOnly? Likely, DateOnly? Earliest, DateOnly? Latest, bool Overdue, double? Renewal, DateTimeOffset? LastAiredUtc);

/// <summary>
/// The waiting room: running shows the caller is caught up on (every aired regular episode watched), with when each is
/// back. Computed on request from the caller's own watches and the catalog copy, plus the hourly show documents'
/// <see cref="ShowInsights.NextPremiere"/> and <see cref="Outlook"/>; ended shows have nothing to wait for.
/// </summary>
public static class WaitingRoomApi
{
    public static void MapWaitingRoomApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/waiting", GetAsync).RequireFeature(FeatureFlags.WaitingRoom);

    private static async Task<Ok<WaitingRoomDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();

        // Caught up: every aired regular episode watched (the aggregate), on a show that's still running.
        var progress = (await Viewer.ProgressAsync(db, userId, now, null, ct)).Values.Where(p => p.AllWatched).Select(p => p.ShowId).ToList();
        var shows = await db.Shows.AsNoTracking()
            .Where(s => progress.Contains(s.ShowId) && s.Status != ShowStatus.Ended && s.Status != ShowStatus.Cancelled)
            .ToDictionaryAsync(s => s.ShowId, ct);
        var caughtUp = shows.Keys.ToList();
        var latest = await Viewer.ProgressAsync(db, userId, now, caughtUp, ct);
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
                var lastAired = latest[id].LastAiredAtUtc;
                var next = upcoming[id].MinBy(e => e.AirsAtUtc);
                var currentSeason = latest[id].LatestSeason;

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

        return TypedResults.Ok(new WaitingRoomDto(result));
    }
}
