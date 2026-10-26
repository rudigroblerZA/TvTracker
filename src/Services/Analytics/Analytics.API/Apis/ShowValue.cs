using Analytics.Data;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

/// <summary>
/// How much a show is likely worth the user's time, for backlog triage and "Fits your time off": the provider's rating,
/// nudged by whether its genres are ones the user likes and by how it ends. A score on the rating's 0-10 scale, so
/// "worth starting" reads as a rating would. Kept deliberately simple and explained by its reasons.
/// </summary>
internal static class ShowValue
{
    /// <summary>Shows without a provider rating count as a typical show.</summary>
    public const double UnratedValue = 7.0;

    /// <summary>All of a show's genres liked adds half this; none of them takes half away.</summary>
    private const double TasteWeight = 0.6;

    /// <summary>A turn this far in (or further) is worth warning about.</summary>
    public const int SlowStartMinutes = 180;

    /// <summary>A show watched this far (of what's aired) counts as one the user liked.</summary>
    private const double FinishedShare = 0.8;

    /// <param name="Landing">The <see cref="LandingVerdicts"/> verdict, for ended shows that have one.</param>
    /// <param name="SlowStart">Where a slow start turns, when it's <see cref="SlowStartMinutes"/> or more in.</param>
    internal sealed record Assessment(double Value, IReadOnlyList<string> Reasons, string? Landing, GetsGoodTurn? SlowStart, bool Limited);

    /// <summary>
    /// Genres of the shows the user liked: rated 4 or 5 stars, or watched at least <see cref="FinishedShare"/> of. With
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
        var finished = watched.Where(w => runs.TryGetValue(w.ShowId, out var run) && w.Count >= run.Episodes * FinishedShare).Select(w => w.ShowId);
        var liked = rated.Union(finished).ToList();

        var genres = await db.Shows.AsNoTracking().Where(s => liked.Contains(s.ShowId)).Select(s => s.Genres).ToListAsync(ct);
        var needed = liked.Count >= 4 ? 2 : 1;
        return genres.SelectMany(g => g).GroupBy(g => g).Where(g => g.Count() >= needed).Select(g => g.Key).ToHashSet();
    }

    internal static Assessment Assess(ShowCopy show, ShowInsights? insights, HashSet<string> liked)
    {
        var reasons = new List<string>();
        var value = show.ExternalRating ?? UnratedValue;
        if (show.ExternalRating is { } rating)
        {
            reasons.Add($"Rated {rating:0.0}");
        }

        if (liked.Count > 0 && show.Genres.Count > 0)
        {
            var shared = show.Genres.Where(liked.Contains).ToList();
            value += TasteWeight * ((double)shared.Count / show.Genres.Count - 0.5);
            if (shared.Count > 0)
            {
                reasons.Add($"Your kind of show: {string.Join(", ", shared)}");
            }
        }

        var landing = insights?.Landing?.Verdict;
        value += landing switch
        {
            LandingVerdicts.Sticks or LandingVerdicts.GreatFinale => 0.3,
            LandingVerdicts.WeakerLastSeason => -0.1,
            LandingVerdicts.FinaleLetsDown => -0.3,
            LandingVerdicts.Fizzles => -0.5,
            _ => 0
        };
        if (landing switch
            {
                LandingVerdicts.Sticks => "Sticks the landing",
                LandingVerdicts.GreatFinale => "Great finale",
                LandingVerdicts.WeakerLastSeason => "Weaker last season",
                LandingVerdicts.FinaleLetsDown => "The finale lets it down",
                LandingVerdicts.Fizzles => "Fizzles out at the end",
                _ => null
            } is { } ending)
        {
            reasons.Add(ending);
        }

        var slowStart = insights?.GetsGood?.Turn is { MinutesBefore: >= SlowStartMinutes } turn ? turn : null;
        if (slowStart is not null)
        {
            reasons.Add($"Slow start: gets good at S{slowStart.SeasonNumber:00}E{slowStart.EpisodeNumber:00}, {Hours(slowStart.MinutesBefore)} in");
        }

        var limited = insights?.Tags?.Any(t => t.Key == ShowTags.LimitedSeries) == true;
        return new Assessment(Math.Round(value, 2), reasons, landing, slowStart, limited);
    }

    /// <summary>
    /// Aired regular episodes and their running time per show. Missing runtimes count as the show's average known one,
    /// as the essential cut does with its typical runtime.
    /// </summary>
    internal static async Task<Dictionary<Guid, (int Episodes, int Minutes)>> RunTimesAsync(AnalyticsContext db, List<Guid> showIds,
        DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
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

    /// <summary>The shows' insights documents, keyed by show.</summary>
    internal static Task<Dictionary<Guid, ShowInsights>> InsightsAsync(AnalyticsContext db, List<Guid> showIds, CancellationToken ct) =>
        FinishOutlookApi.InsightsAsync(db, showIds, ct);

    /// <summary>"45 min", "3 h": hours rounded once past two.</summary>
    internal static string Hours(int minutes) => minutes < 120 ? $"{minutes} min" : $"{Math.Round(minutes / 60.0):N0} h";
}
