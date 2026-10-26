using System.Text.Json;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

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

/// <param name="Value">What the list ranks by: the show's rating (hidden gems), its episode-rating spread (consistency lists)
/// or the finale's rating (best finales).</param>
public record DiscoveryItemDto(
    Guid ShowId,
    string ShowTitle,
    string? PosterUrl,
    double? ShowExternalRating,
    double Value,
    Guid? EpisodeId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle);

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
        api.MapGet("/insights/discover/{list}", GetDiscoveryAsync);
        api.MapGet("/insights/tags", GetTagsAsync);
        api.MapGet("/people/{personId:guid}/insights", GetPersonInsightsAsync);
        api.MapGet("/people/{personId:guid}/fingerprint", GetCreatorFingerprintAsync).RequireFeature(FeatureFlags.CreatorFingerprint);
        api.MapGet("/me/taste", TasteApi.GetTasteAsync);
        api.MapEssentialCutApi();
        api.MapReunionsApi();
        api.MapFinishOutlookApi();
        api.MapTimeMachineApi();
        api.MapRefresherApi();
        api.MapCatchUpApi();

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

    private const int MaxTagShows = 100;

    /// <summary>
    /// The tags of several shows at once (comma-separated ids, at most 100), for show cards and search results.
    /// Shows without insights yet are left out.
    /// </summary>
    private static async Task<Results<Ok<Dictionary<Guid, IReadOnlyList<ShowTag>>>, BadRequest<string>>> GetTagsAsync(
        AnalyticsContext db, string? ids, CancellationToken ct)
    {
        var showIds = (ids ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => Guid.TryParse(id, out var guid) ? guid : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        if (showIds.Length > MaxTagShows)
        {
            return TypedResults.BadRequest($"At most {MaxTagShows} shows at a time.");
        }

        // Only the tags, not the whole documents.
        var rows = await db.Database
            .SqlQuery<TagsRow>($"SELECT \"ShowId\", (\"Document\"->'tags')::text AS \"Tags\" FROM show_insights WHERE \"ShowId\" = ANY({showIds})")
            .ToListAsync(ct);

        return TypedResults.Ok(rows
            .Where(r => r.Tags is not null)
            .ToDictionary(r => r.ShowId,
                r => (IReadOnlyList<ShowTag>)(JsonSerializer.Deserialize<List<ShowTag>>(r.Tags!, JsonOptions) ?? [])));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record TagsRow(Guid ShowId, string? Tags);

    /// <summary>
    /// A creator's fingerprint, as stored (<see cref="CreatorFingerprint"/> JSON). 404 for anyone who didn't create or
    /// develop at least two catalog shows with DNA.
    /// </summary>
    private static async Task<Results<ContentHttpResult, NotFound>> GetCreatorFingerprintAsync(Guid personId, AnalyticsContext db, CancellationToken ct)
    {
        var document = await db.CreatorFingerprints.AsNoTracking()
            .Where(f => f.PersonId == personId)
            .Select(f => f.Document)
            .SingleOrDefaultAsync(ct);

        return document is null ? TypedResults.NotFound() : TypedResults.Text(document, "application/json");
    }

    /// <summary>The stored document is returned as-is: it is already the <see cref="PersonInsights"/> JSON.</summary>
    private static async Task<Results<ContentHttpResult, NotFound>> GetPersonInsightsAsync(Guid personId, AnalyticsContext db, CancellationToken ct)
    {
        var document = await db.PersonInsights.AsNoTracking()
            .Where(i => i.PersonId == personId)
            .Select(i => i.Document)
            .SingleOrDefaultAsync(ct);

        return document is null ? TypedResults.NotFound() : TypedResults.Text(document, "application/json");
    }

    /// <param name="list">One of <see cref="DiscoveryLists.All"/>.</param>
    /// <param name="genre">A genre to rank within; omitted for the overall list.</param>
    private static async Task<Results<Ok<List<DiscoveryItemDto>>, NotFound>> GetDiscoveryAsync(
        string list, AnalyticsContext db, string? genre = null, int limit = 20, CancellationToken ct = default)
    {
        if (!DiscoveryLists.All.Contains(list))
        {
            return TypedResults.NotFound();
        }

        var key = genre?.Trim() ?? "";
        var rows = await db.Discovery.AsNoTracking()
            .Where(d => d.List == list && d.Genre == key)
            .OrderBy(d => d.Rank)
            .Take(Math.Clamp(limit, 1, 30))
            .ToListAsync(ct);

        return TypedResults.Ok(rows
            .Select(d => new DiscoveryItemDto(d.ShowId, d.ShowTitle, d.PosterUrl, d.ShowExternalRating, d.Value,
                d.EpisodeId, d.SeasonNumber, d.EpisodeNumber, d.EpisodeTitle))
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
