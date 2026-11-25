using System.Security.Claims;
using System.Text.Json;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Year in TV (see <see cref="YearInTv"/>), computed on request from the caller's own watches in their profile time zone.
/// </summary>
public static class YearInTvApi
{
    public static void MapYearInTvApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/year", GetYearAsync).RequireFeature(FeatureFlags.YearInTv);

    /// <param name="year">The year to recap; the latest with viewing when missing.</param>
    /// <param name="tz">The caller's time zone (IANA id, from their profile); UTC when missing or unknown.</param>
    private static async Task<Ok<YearInTvDto>> GetYearAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, int? year = null, string? tz = null, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        var zone = Viewer.Zone(tz);
        var now = time.GetUtcNow();
        var today = ViewerRules.LocalDate(now, zone);

        var rows = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .Join(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, e) => new
            {
                w.ShowId,
                w.EpisodeId,
                w.WatchedAtUtc,
                e.RuntimeMinutes,
                e.SeasonNumber,
                e.EpisodeNumber,
                e.Title,
                e.ExternalRating
            })
            .ToListAsync(ct);

        var bulk = ShowFactRules.BulkMarks(rows, r => r.ShowId, r => r.WatchedAtUtc);
        var watches = rows.Where(r => !bulk.Contains((r.ShowId, r.WatchedAtUtc)))
            .Select(r => new YearInTv.Watch(r.ShowId, r.EpisodeId, r.WatchedAtUtc,
                DateOnly.FromDateTime(ViewerRules.Local(r.WatchedAtUtc, zone)),
                r.RuntimeMinutes ?? 0, r.SeasonNumber, r.EpisodeNumber, r.Title, r.ExternalRating))
            .ToList();
        var years = watches.Select(w => w.Day.Year).Distinct().OrderDescending().ToList();
        var shown = year ?? (years.Count > 0 ? years[0] : today.Year);

        var inYear = watches.Where(w => w.Day.Year == shown).ToList();
        var lastYear = watches.Where(w => w.Day.Year == shown - 1).ToList();
        var bulkInYear = rows.Count(r => bulk.Contains((r.ShowId, r.WatchedAtUtc))
            && ViewerRules.Local(r.WatchedAtUtc, zone).Year == shown);

        var showIds = inYear.Select(w => w.ShowId).Concat(lastYear.Select(w => w.ShowId)).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        var ids = inYear.Select(w => w.ShowId).Distinct().Where(shows.ContainsKey).ToList();
        var aired = (await db.Episodes.AsNoTracking().AiredRegular(now)
                .Where(e => ids.Contains(e.ShowId))
                .Select(e => new { e.ShowId, e.EpisodeId, e.AirsAtUtc })
                .ToListAsync(ct))
            .ToLookup(e => e.ShowId, e => (e.EpisodeId, e.AirsAtUtc));
        var progress = YearInTv.Progress(ids, aired, rows.Select(r => (r.ShowId, r.EpisodeId, r.WatchedAtUtc)), shows, inYear,
            shown == today.Year ? now.UtcDateTime : new DateTime(shown, 12, 31, 23, 59, 59, DateTimeKind.Utc));

        return TypedResults.Ok(YearInTv.Of(shown, years, watches, inYear, shows, bulkInYear, progress,
            // Nothing to compare without both years, so their documents aren't read.
            YearInTv.Shift(inYear, lastYear, inYear.Count == 0 || lastYear.Count == 0 ? [] : await DnaAsync(db, showIds, ct)),
            await CalledItAsync(db, userId, shown, zone, ct)));
    }

    /// <summary>
    /// The shows' DNA traits by key, read straight from their documents.
    /// </summary>
    private static async Task<Dictionary<Guid, Dictionary<string, double>>> DnaAsync(AnalyticsContext db, List<Guid> ids, CancellationToken ct)
    {
        var documents = await db.ShowInsights.AsNoTracking().Where(i => ids.Contains(i.ShowId))
            .Select(i => new { i.ShowId, i.Document }).ToListAsync(ct);
        return documents.ToDictionary(d => d.ShowId, d => TraitsOf(d.Document));
    }

    private static Dictionary<string, double> TraitsOf(string document)
    {
        using var json = JsonDocument.Parse(document);
        return json.RootElement.TryGetProperty(InsightDocuments.Paths.Dna, out var dna) && dna.ValueKind == JsonValueKind.Object
               && dna.TryGetProperty(InsightDocuments.Paths.Traits, out var traits) && traits.ValueKind == JsonValueKind.Array
            ? traits.EnumerateArray().ToDictionary(t => t.GetProperty(InsightDocuments.Paths.Key).GetString()!, t => t.GetProperty(InsightDocuments.Paths.Value).GetDouble())
            : [];
    }

    /// <summary>
    /// The show followed that year that they were furthest ahead of its other followers on (see the summary).
    /// </summary>
    private static async Task<YearCalledItDto?> CalledItAsync(AnalyticsContext db, string userId, int year, TimeZoneInfo zone, CancellationToken ct)
    {
        var from = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, 1, 1), zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year + 1, 1, 1), zone);
        var mine = await db.Follows.AsNoTracking()
            .Where(f => f.UserId == userId && f.FollowedAtUtc >= from && f.FollowedAtUtc < to)
            .Join(db.Shows.Where(s => s.ExternalRating >= YearInTv.CalledItMinRating), f => f.ShowId, s => s.ShowId, (f, s) => new { f.FollowedAtUtc, Show = s })
            .ToListAsync(ct);
        if (mine.Count == 0)
        {
            return null;
        }

        var ids = mine.Select(m => m.Show.ShowId).ToList();
        var others = (await db.Follows.AsNoTracking()
                .Where(f => ids.Contains(f.ShowId) && f.UserId != userId)
                .Select(f => new { f.ShowId, f.FollowedAtUtc })
                .ToListAsync(ct))
            .ToLookup(f => f.ShowId, f => f.FollowedAtUtc);

        return YearInTv.CalledIt(mine.Select(m => (m.Show, m.FollowedAtUtc)), others);
    }
}
