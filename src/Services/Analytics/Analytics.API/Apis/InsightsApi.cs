using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

public record PremiereDto(
    Guid ShowId,
    string ShowTitle,
    string? PosterUrl,
    IReadOnlyList<string> Genres,
    int SeasonNumber,
    DateTimeOffset PremieresAtUtc,
    double? ShowExternalRating,
    int? DaysSincePreviousSeason,
    int CatchUpEpisodes,
    int CatchUpRuntimeMinutes,
    int? RecentFollows);

public record TopEpisodeDto(
    Guid EpisodeId,
    Guid ShowId,
    string ShowTitle,
    string? PosterUrl,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    DateTimeOffset? AirsAtUtc,
    RatingPair Rating);

/// <summary>
/// Insights for signed-in users only. Everything is computed hourly by Analytics.Worker; these endpoints just read it.
/// Our users' averages are only included once enough ratings back them (see <see cref="InsightRules"/>).
/// </summary>
public static class InsightsApi
{
    public static RouteGroupBuilder MapInsightsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/shows/{showId:guid}/insights", GetShowInsightsAsync);
        api.MapGet("/insights/premieres", GetPremieresAsync);
        api.MapGet("/insights/top-episodes", GetTopEpisodesAsync);

        return api;
    }

    /// <summary>The stored document is returned as-is: it is already the <see cref="ShowInsights"/> JSON.</summary>
    private static async Task<Results<ContentHttpResult, NotFound>> GetShowInsightsAsync(Guid showId, AnalyticsContext db, CancellationToken ct)
    {
        var document = await db.ShowInsights.AsNoTracking()
            .Where(i => i.ShowId == showId)
            .Select(i => i.Document)
            .SingleOrDefaultAsync(ct);

        return document is null ? TypedResults.NotFound() : TypedResults.Text(document, "application/json");
    }

    /// <param name="sort">
    /// "date" (soonest first, the default), "rating" (the show's provider rating, highest first) or
    /// "anticipated" (most follows from our users in the last four weeks first).
    /// </param>
    private static async Task<Ok<List<PremiereDto>>> GetPremieresAsync(
        AnalyticsContext db, TimeProvider time, int days = 60, string? sort = null, int limit = 50, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var until = now.AddDays(Math.Clamp(days, 1, 90));

        var query = db.Premieres.AsNoTracking().Where(p => p.PremieresAtUtc > now && p.PremieresAtUtc <= until);
        query = sort?.ToLowerInvariant() switch
        {
            "rating" => query.OrderByDescending(p => p.ShowExternalRating ?? 0).ThenBy(p => p.PremieresAtUtc),
            "anticipated" => query.OrderByDescending(p => p.RecentFollows ?? 0).ThenBy(p => p.PremieresAtUtc),
            _ => query.OrderBy(p => p.PremieresAtUtc)
        };

        var rows = await query.Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
        return TypedResults.Ok(rows
            .Select(p => new PremiereDto(p.ShowId, p.ShowTitle, p.PosterUrl, p.Genres, p.SeasonNumber, p.PremieresAtUtc,
                p.ShowExternalRating, p.DaysSincePreviousSeason, p.CatchUpEpisodes, p.CatchUpRuntimeMinutes, p.RecentFollows))
            .ToList());
    }

    /// <param name="genre">A genre to rank within; omitted for the overall ranking.</param>
    private static async Task<Ok<List<TopEpisodeDto>>> GetTopEpisodesAsync(
        AnalyticsContext db, string? genre = null, int limit = 20, CancellationToken ct = default)
    {
        var key = genre?.Trim() ?? "";
        var rows = await db.TopEpisodes.AsNoTracking()
            .Where(t => t.Genre == key)
            .OrderBy(t => t.Rank)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(ct);

        return TypedResults.Ok(rows
            .Select(t => new TopEpisodeDto(t.EpisodeId, t.ShowId, t.ShowTitle, t.PosterUrl, t.SeasonNumber, t.EpisodeNumber, t.Title,
                t.AirsAtUtc, new RatingPair(t.ExternalRating, t.OurRating, t.OurRatingCount)))
            .ToList());
    }
}
