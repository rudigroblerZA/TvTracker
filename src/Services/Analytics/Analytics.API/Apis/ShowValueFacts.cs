using Analytics.Data;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

/// <summary>
/// What <see cref="ShowValue"/> is worked out from, loaded for a viewer: the genres they like, shows' running times and documents.
/// </summary>
internal static class ShowValueFacts
{
    /// <summary>
    /// Genres of the shows the user liked: rated 4 or 5 stars, or watched at least <see cref="ShowValue.FinishedShare"/> of. With
    /// four or more such shows, a genre needs two of them, so one stray show doesn't define a taste.
    /// </summary>
    internal static async Task<HashSet<string>> LikedGenresAsync(AnalyticsContext db, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var rated = await db.ShowRatings.AsNoTracking().Where(r => r.UserId == userId && r.Stars >= 4).Select(r => r.ShowId).ToListAsync(ct);
        var watched = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .GroupBy(w => w.ShowId)
            .Select(g => new { ShowId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var runs = await RunTimesAsync(db, watched.Select(w => w.ShowId).ToList(), now, ct);
        var finished = watched.Where(w => runs.TryGetValue(w.ShowId, out var run) && w.Count >= run.Episodes * ShowValue.FinishedShare).Select(w => w.ShowId);
        var liked = rated.Union(finished).ToList();

        var genres = await db.Shows.AsNoTracking().Where(s => liked.Contains(s.ShowId)).Select(s => s.Genres).ToListAsync(ct);
        var needed = liked.Count >= 4 ? 2 : 1;
        return genres.SelectMany(g => g).GroupBy(g => g).Where(g => g.Count() >= needed).Select(g => g.Key).ToHashSet();
    }

    /// <summary>
    /// Aired regular episodes and their running time per show, in one aggregate. Missing runtimes count as the show's
    /// average known one (the mean, which SQL can give for many shows at once; the essential cut, with one show's
    /// episodes in memory, uses the median).
    /// </summary>
    internal static async Task<Dictionary<Guid, (int Episodes, int Minutes)>> RunTimesAsync(AnalyticsContext db, List<Guid> showIds,
        DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Episodes.AsNoTracking()
            .AiredRegular(now)
            .Where(e => showIds.Contains(e.ShowId))
            .GroupBy(e => e.ShowId)
            .Select(g => new
            {
                ShowId = g.Key,
                Count = g.Count(),
                Known = g.Count(e => e.RuntimeMinutes > 0),
                Sum = g.Sum(e => e.RuntimeMinutes > 0 ? e.RuntimeMinutes!.Value : 0)
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.ShowId,
            r => (r.Count, r.Known == 0 ? 0 : (int)Math.Round(r.Sum + (r.Count - r.Known) * (double)r.Sum / r.Known)));
    }

    /// <summary>
    /// The shows' insights documents, keyed by show.
    /// </summary>
    internal static Task<Dictionary<Guid, ShowInsights>> InsightsAsync(AnalyticsContext db, List<Guid> showIds, CancellationToken ct) =>
        ShowDocuments.LoadAsync(db, showIds, ct);
}
