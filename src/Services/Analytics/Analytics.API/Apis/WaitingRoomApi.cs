using System.Security.Claims;
using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<Results<Ok<WaitingRoomDto>, UnauthorizedHttpResult>> GetAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var now = time.GetUtcNow();

        var watched = await db.Watches.AsNoTracking().Where(w => w.UserId == userId).Select(w => new { w.ShowId, w.EpisodeId }).ToListAsync(ct);
        var showIds = watched.Select(w => w.ShowId).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking()
            .Where(s => showIds.Contains(s.ShowId) && s.Status != ShowStatus.Ended && s.Status != ShowStatus.Cancelled)
            .ToDictionaryAsync(s => s.ShowId, ct);
        var running = shows.Keys.ToList();
        var episodes = (await db.Episodes.AsNoTracking()
                .Where(e => running.Contains(e.ShowId) && e.SeasonNumber > 0)
                .Select(e => new { e.ShowId, e.EpisodeId, e.SeasonNumber, e.AirsAtUtc })
                .ToListAsync(ct))
            .ToLookup(e => e.ShowId);
        var seen = watched.Select(w => w.EpisodeId).ToHashSet();

        var caughtUp = running.Where(id =>
            {
                var aired = episodes[id].Where(e => e.AirsAtUtc <= now).ToList();
                return aired.Count > 0 && aired.All(e => seen.Contains(e.EpisodeId));
            })
            .ToList();
        var documents = await db.ShowInsights.AsNoTracking().Where(i => caughtUp.Contains(i.ShowId))
            .ToDictionaryAsync(i => i.ShowId, i => i.Document, ct);

        var result = caughtUp.Select(id =>
            {
                var show = shows[id];
                var insights = documents.TryGetValue(id, out var json) ? JsonSerializer.Deserialize<ShowInsights>(json, JsonOptions) : null;
                var lastAired = episodes[id].Where(e => e.AirsAtUtc <= now).Max(e => e.AirsAtUtc);
                var next = episodes[id].Where(e => e.AirsAtUtc > now).MinBy(e => e.AirsAtUtc);
                var currentSeason = episodes[id].Where(e => e.AirsAtUtc <= now).Max(e => e.SeasonNumber);

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
