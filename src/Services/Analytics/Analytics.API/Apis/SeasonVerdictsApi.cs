using System.Text.Json;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Analytics.API.Apis;

public record SeasonVerdictItem(Guid ShowId, string Title, SeasonVerdict Verdict);

/// <summary>
/// Seasons that just finished (<see cref="SeasonVerdict"/>), across shows: read from the stored show documents, newest
/// finale first. The worker drops a verdict <see cref="SeasonVerdict.Until"/> after the finale; the window is checked here
/// too, so a stalled pipeline doesn't leave old seasons listed as just ended.
/// <para>
/// Cached for <see cref="CacheFor"/>: the documents change once an hour, and reading every show's document (the largest
/// values Analytics stores) on each Insights visit cost tens of milliseconds.
/// </para>
/// </summary>
public static class SeasonVerdictsApi
{
    private const int MaxLimit = 50;
    private static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(5) };

    public static void MapSeasonVerdictsApi(this RouteGroupBuilder api) =>
        api.MapGet("/insights/season-verdicts", GetAsync).RequireFeature(FeatureFlags.SeasonVerdict);

    private static async Task<Ok<List<SeasonVerdictItem>>> GetAsync(AnalyticsContext db, HybridCache cache, TimeProvider time, int? limit,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 20, 1, MaxLimit);
        return TypedResults.Ok(await cache.GetOrCreateAsync($"analytics:season-verdicts:{take}",
            async token => await LoadAsync(db, time.GetUtcNow() - SeasonVerdict.Until, take, token), CacheFor, cancellationToken: ct));
    }

    private static async Task<List<SeasonVerdictItem>> LoadAsync(AnalyticsContext db, DateTimeOffset since, int take, CancellationToken ct)
    {
        // Each document's verdict is taken out once (the inner query); the rest reads only that.
        var rows = await db.Database
            .SqlQuery<VerdictRow>($"""
                SELECT "ShowId", "Title", "Verdict"::text AS "Verdict"
                FROM (SELECT "ShowId", "Document"->>'title' AS "Title", "Document"->'seasonVerdict' AS "Verdict"
                      FROM show_insights) v
                WHERE jsonb_typeof("Verdict") = 'object'
                  AND ("Verdict"->>'finaleAiredAtUtc')::timestamptz >= {since}
                ORDER BY ("Verdict"->>'finaleAiredAtUtc')::timestamptz DESC
                LIMIT {take}
                """)
            .ToListAsync(ct);

        return rows
            .Select(r => new SeasonVerdictItem(r.ShowId, r.Title, JsonSerializer.Deserialize<SeasonVerdict>(r.Verdict, JsonSerializerOptions.Web)!))
            .ToList();
    }

    private sealed record VerdictRow(Guid ShowId, string Title, string Verdict);
}
