using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Streaks (see <see cref="Streaks"/>), from the caller's own watches in their profile time zone (<c>tz</c>).
/// </summary>
public static class StreaksApi
{
    private const int MaxShows = 12;

    public static void MapStreaksApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/streaks", GetAsync).RequireFeature(FeatureFlags.Streaks);

    /// <param name="tz">The caller's time zone (IANA id, from their profile); UTC when missing or unknown.</param>
    private static async Task<Ok<StreaksDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, string? tz = null,
        CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var zone = Viewer.Zone(tz);
        var now = time.GetUtcNow();
        var today = ViewerRules.LocalDate(now, zone);

        var rows = await db.Watches.AsNoTracking().Where(w => w.UserId == userId).ToListAsync(ct);
        var bulk = ShowFactRules.BulkMarks(rows);
        var watches = rows.Where(r => !bulk.Contains((r.ShowId, r.WatchedAtUtc))).ToList();

        var (dayStreak, weekStreak) = Streaks.Calendar(watches.Select(w => DateOnly.FromDateTime(ViewerRules.Local(w.WatchedAtUtc, zone))), today);

        // Kept up: the regular aired episodes of the shows the caller watched outside bulk marks, in air order. An episode
        // can only be kept if it aired after the earliest watch less the window, so a back catalog watched years later isn't loaded.
        var showIds = watches.Select(w => w.ShowId).Distinct().ToList();
        var since = watches.Count > 0 ? new DateTimeOffset(watches.Min(w => w.WatchedAtUtc), TimeSpan.Zero).AddDays(-Streaks.KeptUpDays) : now;
        var aired = await db.Episodes.AsNoTracking().AiredRegular(now)
            .Where(e => showIds.Contains(e.ShowId) && e.AirsAtUtc >= since)
            .Select(e => new Streaks.Aired(e.EpisodeId, e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.AirsAtUtc!.Value))
            .ToListAsync(ct);
        var watchedAt = watches.GroupBy(w => w.EpisodeId).ToDictionary(g => g.Key, g => new DateTimeOffset(g.Min(w => w.WatchedAtUtc), TimeSpan.Zero));

        var (keptUp, due, best) = Streaks.AcrossShows(aired, watchedAt, now, zone);

        // Titles and posters for the shows with a streak, not every show the caller ever watched.
        var shown = keptUp.Select(k => k.ShowId).Concat(due.Select(d => d.ShowId)).Concat(best is null ? [] : new[] { best.ShowId }).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => shown.Contains(s.ShowId))
            .Select(s => new { s.ShowId, s.Title, s.PosterUrl }).ToDictionaryAsync(s => s.ShowId, ct);
        KeptUpDto Name(KeptUpDto k) => k with { Title = shows[k.ShowId].Title, PosterUrl = shows[k.ShowId].PosterUrl };

        return TypedResults.Ok(new StreaksDto(Streaks.KeptUpDays, rows.Count - watches.Count, dayStreak, weekStreak,
            keptUp.Where(k => shows.ContainsKey(k.ShowId)).Select(Name)
                .OrderByDescending(k => k.Current).ThenBy(k => k.Title, StringComparer.Ordinal).Take(MaxShows).ToList(),
            due.Where(d => shows.ContainsKey(d.ShowId)).Select(d => d with { Title = shows[d.ShowId].Title, PosterUrl = shows[d.ShowId].PosterUrl })
                .OrderBy(d => d.WatchBy).ThenByDescending(d => d.Streak).ThenBy(d => d.Season).ThenBy(d => d.Episode).Take(MaxShows).ToList(),
            best is not null && shows.ContainsKey(best.ShowId) ? Name(best) : null));
    }
}
