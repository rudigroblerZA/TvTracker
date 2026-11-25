using Analytics.Data;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

/// <summary>
/// What <see cref="ShowValue"/> is worked out from, loaded for a viewer: the genres they like, and shows' running times.
/// </summary>
internal static class ShowValueFacts
{
    /// <summary>
    /// Genres of the shows the user liked (<see cref="ShowValue.LikedShows"/>, <see cref="ShowValue.LikedGenres"/>).
    /// </summary>
    internal static async Task<HashSet<string>> LikedGenresAsync(AnalyticsContext db, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var stars = await db.ShowRatings.AsNoTracking().Where(r => r.UserId == userId && r.Stars >= ShowValue.LikedStars)
            .ToDictionaryAsync(r => r.ShowId, r => r.Stars, ct);
        var watched = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId)
            .GroupBy(w => w.ShowId)
            .Select(g => new { ShowId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(w => w.ShowId, w => w.Count, ct);
        var runs = await RunTimesAsync(db, watched.Keys.ToList(), now, ct);
        var liked = ShowValue.LikedShows(stars, watched, runs.ToDictionary(r => r.Key, r => r.Value.Episodes));

        return ShowValue.LikedGenres(await db.Shows.AsNoTracking().Where(s => liked.Contains(s.ShowId)).Select(s => s.Genres).ToListAsync(ct));
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
}
