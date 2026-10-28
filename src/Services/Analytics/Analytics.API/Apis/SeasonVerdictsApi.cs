using System.Text.Json;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

public record SeasonVerdictItem(Guid ShowId, string Title, SeasonVerdict Verdict);

/// <summary>
/// Seasons that just finished (<see cref="SeasonVerdict"/>), across shows: read from the stored show documents, newest
/// finale first. The worker drops a verdict 45 days after the finale; the window is checked here too, so a stalled
/// pipeline doesn't leave old seasons listed as just ended.
/// </summary>
public static class SeasonVerdictsApi
{
    private const int MaxLimit = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapSeasonVerdictsApi(this RouteGroupBuilder api) =>
        api.MapGet("/insights/season-verdicts", GetAsync).RequireFeature(FeatureFlags.SeasonVerdict);

    private static async Task<Ok<List<SeasonVerdictItem>>> GetAsync(AnalyticsContext db, TimeProvider time, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 20, 1, MaxLimit);
        var since = time.GetUtcNow().AddDays(-45);
        // Only the verdicts, not the whole documents.
        var rows = await db.Database
            .SqlQuery<VerdictRow>($"""
                SELECT "ShowId", "Document"->>'title' AS "Title", ("Document"->'seasonVerdict')::text AS "Verdict"
                FROM show_insights
                WHERE jsonb_typeof("Document"->'seasonVerdict') = 'object'
                  AND ("Document"->'seasonVerdict'->>'finaleAiredAtUtc')::timestamptz >= {since}
                ORDER BY "Document"->'seasonVerdict'->>'finaleAiredAtUtc' DESC
                LIMIT {take}
                """)
            .ToListAsync(ct);

        return TypedResults.Ok(rows
            .Select(r => new SeasonVerdictItem(r.ShowId, r.Title, JsonSerializer.Deserialize<SeasonVerdict>(r.Verdict, JsonOptions)!))
            .ToList());
    }

    private sealed record VerdictRow(Guid ShowId, string Title, string Verdict);
}
