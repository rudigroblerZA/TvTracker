using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Research.API.Data;
using TvTracker;

namespace Research.API.Apis;

/// <param name="Wins">Award wins on Wikidata; null for a researched show outside the popular shows checked.</param>
/// <param name="Summary">Grouped by award, most won first.</param>
/// <param name="Checked">From saved research, checked by a moderator; otherwise straight from Wikidata.</param>
/// <param name="SourceUrl">The Wikidata item, or null for saved research (its page cites the sources).</param>
public record AwardedShowDto(Guid ShowId, string Title, string? PosterUrl, int? Wins, string Summary, bool Checked, string? SourceUrl);

/// <summary>
/// The awards shelf: what a show has won, and the most-awarded popular shows (Prestige picks). Wins come from the daily
/// Wikidata check (<see cref="Services.CatalogChecks"/>); where a moderator saved research with an awards fact, its
/// checked wording replaces Wikidata's. Public, like the shows; the UI leaves out what the user has started.
/// </summary>
public static class AwardsApi
{
    private const int MaxList = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapAwardsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/awards", GetMostAwardedAsync).RequireFeature(FeatureFlags.AwardsShelf);
        app.MapGet("/api/v1/shows/{showId:guid}/awards", GetShowAwardsAsync).RequireFeature(FeatureFlags.AwardsShelf);
    }

    private static async Task<Ok<List<AwardedShowDto>>> GetMostAwardedAsync(ResearchContext db, int limit = 50, CancellationToken ct = default)
    {
        var shows = await db.ShowAwards.AsNoTracking()
            .OrderByDescending(a => a.Wins).ThenBy(a => a.ShowTitle)
            .Take(Math.Clamp(limit, 1, MaxList))
            .ToListAsync(ct);
        var checkedAwards = await CheckedAsync(db, shows.Select(s => s.ShowId).ToList(), ct);
        return TypedResults.Ok(shows.Select(a => ToDto(a, checkedAwards.TryGetValue(a.ShowId, out var c) ? c : null)).ToList());
    }

    private static async Task<Results<Ok<AwardedShowDto>, NotFound>> GetShowAwardsAsync(Guid showId, ResearchContext db, CancellationToken ct)
    {
        var award = await db.ShowAwards.AsNoTracking().SingleOrDefaultAsync(a => a.ShowId == showId, ct);
        // TryGetValue, not GetValueOrDefault: a missing tuple would come back as an empty one, not null.
        (string Title, string Summary)? checkedAward = (await CheckedAsync(db, [showId], ct)).TryGetValue(showId, out var c) ? c : null;
        if (award is not null)
        {
            return TypedResults.Ok(ToDto(award, checkedAward));
        }
        return checkedAward is { } found
            ? TypedResults.Ok(new AwardedShowDto(showId, found.Title, null, null, found.Summary, true, null))
            : TypedResults.NotFound();
    }

    private static AwardedShowDto ToDto(ShowAward a, (string Title, string Summary)? checkedAward) => checkedAward is { } c
        ? new(a.ShowId, a.ShowTitle, a.PosterUrl, a.Wins, c.Summary, true, null)
        : new(a.ShowId, a.ShowTitle, a.PosterUrl, a.Wins, a.Summary, false, a.WikidataUrl);

    /// <summary>The awards fact of each show's latest saved research, where it has one.</summary>
    private static async Task<Dictionary<Guid, (string Title, string Summary)>> CheckedAsync(ResearchContext db, List<Guid> showIds,
        CancellationToken ct)
    {
        var latest = await db.Saved.AsNoTracking()
            .Where(s => showIds.Contains(s.ShowId))
            .GroupBy(s => s.ShowId)
            .Select(g => g.OrderByDescending(s => s.Version).First())
            .ToListAsync(ct);
        var titles = await db.Dossiers.AsNoTracking().Where(d => showIds.Contains(d.ShowId)).ToDictionaryAsync(d => d.ShowId, d => d.ShowTitle, ct);
        var found = new Dictionary<Guid, (string, string)>();
        foreach (var saved in latest)
        {
            var awards = (JsonSerializer.Deserialize<List<SavedFact>>(saved.Facts, Json) ?? []).FirstOrDefault(f => f.Key == "awards");
            if (awards is { Value.Length: > 0 })
            {
                found[saved.ShowId] = (titles.GetValueOrDefault(saved.ShowId) ?? "", awards.Value);
            }
        }
        return found;
    }
}
