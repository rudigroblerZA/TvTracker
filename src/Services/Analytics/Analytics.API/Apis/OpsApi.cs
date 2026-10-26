using Analytics.Data;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

/// <summary>Counts only: how many users rated, watched or followed, never who.</summary>
public record AnalyticsOpsStats(
    int Shows,
    int ShowsWithInsights,
    DateTime? LastComputedAtUtc,
    int SharedCastLinks,
    int ShowRatings,
    int EpisodeRatings,
    int Watches,
    int Follows,
    int ActiveUsers,
    IReadOnlyList<OpsTopEpisode> TopEpisodes,
    IReadOnlyList<OpsPremiere> UpcomingPremieres,
    IReadOnlyList<OpsConnectedShow> MostConnected,
    OpsComputed Computed,
    IReadOnlyList<OpsWorthTheWait> WorthTheWait,
    int Episodes);

/// <summary>How many shows each part of the insights covers, and the other computed tables.</summary>
/// <param name="GetsGoodTurns">Slow starters with a turn ("gets good at S01E06").</param>
/// <param name="GoodFromTheStart">Shows whose first episodes already rate as well as the rest.</param>
/// <param name="Forecasts">Running shows with at least one forecast (rating, renewal or timing).</param>
/// <param name="CastCreditCopies">The provider's cast credits copied for the reunion radar.</param>
public record OpsComputed(int ShowsWithDna, int GetsGoodTurns, int GoodFromTheStart, int Forecasts, int CreatorFingerprints,
    int ActorInsights, int CastCreditCopies);

/// <param name="Lift">How much better it rates from the turn than before it.</param>
public record OpsWorthTheWait(Guid ShowId, string ShowTitle, int SeasonNumber, int EpisodeNumber, double Lift);

public record OpsTopEpisode(Guid ShowId, string ShowTitle, int SeasonNumber, int EpisodeNumber, string Title, double ExternalRating);

public record OpsPremiere(Guid ShowId, string ShowTitle, int SeasonNumber, DateTimeOffset PremieresAtUtc);

/// <param name="SharedCast">How many shared-cast links (actors also in another show) its insights list.</param>
public record OpsConnectedShow(Guid ShowId, string Title, int SharedCast);

/// <summary>
/// Stats for the developer ops dashboard that Catalog.API serves. Mapped in Development only and without sign-in,
/// since Catalog.API calls it server-side; the mobile gateway's /analytics route would otherwise expose it.
/// </summary>
public static class OpsApi
{
    public static void MapOpsApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/stats", GetStatsAsync);

    private static async Task<AnalyticsOpsStats> GetStatsAsync(AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        var links = await db.Database
            .SqlQueryRaw<ShowLinks>(
                "SELECT \"ShowId\", COALESCE(jsonb_array_length(\"Document\"->'sharedCast'), 0) AS \"Links\" FROM show_insights")
            .ToListAsync(ct);
        var connectedIds = links.Where(l => l.Links > 0).OrderByDescending(l => l.Links).Take(5).ToList();
        var titles = await db.Shows
            .Where(s => connectedIds.Select(c => c.ShowId).Contains(s.ShowId))
            .ToDictionaryAsync(s => s.ShowId, s => s.Title, ct);

        var users = await db.ShowRatings.Select(r => r.UserId)
            .Union(db.EpisodeRatings.Select(r => r.UserId))
            .Union(db.Watches.Select(w => w.UserId))
            .Union(db.Follows.Select(f => f.UserId))
            .CountAsync(ct);

        return new AnalyticsOpsStats(
            Shows: await db.Shows.CountAsync(ct),
            ShowsWithInsights: links.Count,
            LastComputedAtUtc: await db.ShowInsights.MaxAsync(i => (DateTime?)i.ComputedAtUtc, ct),
            SharedCastLinks: links.Sum(l => l.Links),
            ShowRatings: await db.ShowRatings.CountAsync(ct),
            EpisodeRatings: await db.EpisodeRatings.CountAsync(ct),
            Watches: await db.Watches.CountAsync(ct),
            Follows: await db.Follows.CountAsync(ct),
            ActiveUsers: users,
            TopEpisodes: await db.TopEpisodes
                .Where(t => t.Genre == "")
                .OrderBy(t => t.Rank)
                .Take(5)
                .Select(t => new OpsTopEpisode(t.ShowId, t.ShowTitle, t.SeasonNumber, t.EpisodeNumber, t.Title, t.ExternalRating))
                .ToListAsync(ct),
            UpcomingPremieres: await db.Premieres
                .Where(p => p.PremieresAtUtc > now)
                .OrderBy(p => p.PremieresAtUtc)
                .Take(5)
                .Select(p => new OpsPremiere(p.ShowId, p.ShowTitle, p.SeasonNumber, p.PremieresAtUtc))
                .ToListAsync(ct),
            MostConnected: connectedIds
                .Select(c => new OpsConnectedShow(c.ShowId, titles.GetValueOrDefault(c.ShowId, "?"), c.Links))
                .ToList(),
            Computed: await GetComputedAsync(db, ct),
            WorthTheWait: await db.Discovery
                .Where(d => d.List == DiscoveryLists.WorthTheWait && d.Genre == "")
                .OrderBy(d => d.Rank)
                .Take(5)
                .Select(d => new OpsWorthTheWait(d.ShowId, d.ShowTitle, d.SeasonNumber ?? 0, d.EpisodeNumber ?? 0, d.Value))
                .ToListAsync(ct),
            Episodes: await db.Episodes.CountAsync(ct));
    }

    private static async Task<OpsComputed> GetComputedAsync(AnalyticsContext db, CancellationToken ct)
    {
        // One pass over the documents; the parts are JSON objects when present and null otherwise.
        var parts = await db.Database
            .SqlQueryRaw<DocumentParts>(
                "SELECT COUNT(*) FILTER (WHERE jsonb_typeof(\"Document\"->'dna') = 'object')::int AS \"Dna\", " +
                "COUNT(*) FILTER (WHERE jsonb_typeof(\"Document\"->'getsGood'->'turn') = 'object')::int AS \"Turns\", " +
                "COUNT(*) FILTER (WHERE (\"Document\"->'getsGood'->>'fromTheStart') = 'true')::int AS \"FromTheStart\", " +
                "COUNT(*) FILTER (WHERE jsonb_typeof(\"Document\"->'outlook') = 'object')::int AS \"Forecasts\" " +
                "FROM show_insights")
            .SingleAsync(ct);

        return new OpsComputed(parts.Dna, parts.Turns, parts.FromTheStart, parts.Forecasts,
            await db.CreatorFingerprints.CountAsync(ct), await db.PersonInsights.CountAsync(ct), await db.CastCredits.CountAsync(ct));
    }

    private sealed record ShowLinks(Guid ShowId, int Links);

    private sealed record DocumentParts(int Dna, int Turns, int FromTheStart, int Forecasts);
}
