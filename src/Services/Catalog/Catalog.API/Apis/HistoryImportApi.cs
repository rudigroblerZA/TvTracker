using System.Security.Claims;
using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Key">The client's handle for the show (its TheTVDB id or title), given back unchanged.</param>
/// <param name="Episodes">The season and episode numbers the client has watches for, to get their ids.</param>
public record HistoryShowRequest(string Key, string? TvdbId, string? Title, int? Year, IReadOnlyList<HistoryEpisodeNumber> Episodes);

public record HistoryEpisodeNumber(int Season, int Number);

public record HistoryResolveRequest(IReadOnlyList<HistoryShowRequest> Shows);

/// <param name="Outcome">"found", "imported", "not-found" or "failed" (try that show again later).</param>
/// <param name="Episodes">The ids of the requested episodes the catalog has; a number it lacks is left out.</param>
public record HistoryShowDto(string Key, Guid? ShowId, string? Title, string? PosterUrl, string Outcome, IReadOnlyList<HistoryEpisodeDto> Episodes);

public record HistoryEpisodeDto(int Season, int Number, Guid EpisodeId);

public record HistoryResolveResult(IReadOnlyList<HistoryShowDto> Shows);

/// <summary>
/// Bring your history: turns the shows and episode numbers of a TV Time, TV Time Out or Trakt export into catalog ids, so
/// the page can mark them watched in Tracking with their original dates. The sync worker finds each show by TheTVDB id or
/// title and imports it when the catalog lacks it (<see cref="CatalogImportClient.ResolveAsync"/>); this maps the result to
/// catalog show and episode ids by season and episode number.
/// <list type="bullet">
/// <item><b>Small batches:</b> at most <see cref="CatalogImportClient.MaxResolve"/> shows a call, since a show the catalog
/// lacks costs two provider calls on a shared limiter; the page walks an export in batches and shows progress.</item>
/// <item><b>Signed in and rate limited</b> like search: it can import shows.</item>
/// <item><b>Nothing of the user's is kept:</b> the catalog never sees the watches; Tracking does.</item>
/// </list>
/// </summary>
public static class HistoryImportApi
{
    private const string Provider = "tvmaze";

    public static void MapHistoryImportApi(this RouteGroupBuilder api) =>
        api.MapPost("/history/resolve", ResolveAsync)
            .RequireAuthorization()
            .RequireRateLimiting(SearchRateLimiting.Policy)
            .RequireFeature(FeatureFlags.HistoryImport);

    private static async Task<Results<Ok<HistoryResolveResult>, BadRequest<string>>> ResolveAsync(HistoryResolveRequest request, ClaimsPrincipal user,
        CatalogContext db, CatalogImportClient importer, CancellationToken ct)
    {
        if (request.Shows is not { Count: > 0 and <= CatalogImportClient.MaxResolve })
        {
            return TypedResults.BadRequest($"Give 1 to {CatalogImportClient.MaxResolve} shows.");
        }

        var resolved = await importer.ResolveAsync(request.Shows
            .Select(s => new ResolveShowRequest(s.Key, s.TvdbId, s.Title, s.Year)).ToList(), ct);
        var externalIds = resolved.Where(r => r.ExternalId is not null).Select(r => r.ExternalId!).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking()
            .Where(s => s.Source == Provider && externalIds.Contains(s.ExternalId))
            .Select(s => new { s.ExternalId, s.Id, s.Title, s.PosterUrl })
            .ToDictionaryAsync(s => s.ExternalId, ct);
        var showIds = shows.Values.Select(s => s.Id).ToList();
        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => showIds.Contains(e.ShowId))
            .Select(e => new { e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Id })
            .ToListAsync(ct);
        var byNumber = episodes.GroupBy(e => e.ShowId)
            .ToDictionary(g => g.Key, g => g.GroupBy(e => (e.SeasonNumber, e.EpisodeNumber)).ToDictionary(x => x.Key, x => x.First().Id));

        var result = request.Shows.Select(wanted =>
        {
            var r = resolved.FirstOrDefault(x => x.Key == wanted.Key);
            if (r?.ExternalId is null || !shows.TryGetValue(r.ExternalId, out var show))
            {
                return new HistoryShowDto(wanted.Key, null, null, null, r?.Outcome ?? "failed", []);
            }
            var numbers = byNumber.GetValueOrDefault(show.Id) ?? [];
            var found = wanted.Episodes.Distinct()
                .Where(n => numbers.ContainsKey((n.Season, n.Number)))
                .Select(n => new HistoryEpisodeDto(n.Season, n.Number, numbers[(n.Season, n.Number)]))
                .ToList();
            return new HistoryShowDto(wanted.Key, show.Id, show.Title, show.PosterUrl, r.Outcome, found);
        }).ToList();
        return TypedResults.Ok(new HistoryResolveResult(result));
    }
}
