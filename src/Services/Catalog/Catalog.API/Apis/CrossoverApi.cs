using Catalog.API.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using TvTracker;

namespace Catalog.API.Apis;

public record CrossoverPartDto(Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber, int EpisodeNumber,
    string Title, DateTimeOffset AirsAtUtc);

/// <param name="UniverseId">The universe's hub show, which addresses its page and watch order.</param>
/// <param name="Parts">In air order.</param>
public record CrossoverDto(Guid Id, string Name, Guid UniverseId, string UniverseName, DateTimeOffset StartsAtUtc, IReadOnlyList<CrossoverPartDto> Parts);

public record CrossoverShowsRequest(IReadOnlyList<Guid> ShowIds);

/// <summary>
/// Crossover events coming up in the next 30 days (see <see cref="Crossovers"/>). Public, like the rest of the catalog;
/// the UI posts the user's follows to get theirs (a follow list doesn't fit in a URL), as Holiday episodes does.
/// </summary>
public static class CrossoverApi
{
    public static void MapCrossoverApi(this RouteGroupBuilder api)
    {
        api.MapGet("/crossovers", GetUpcomingAsync).RequireFeature(FeatureFlags.CrossoverAlerts);
        api.MapPost("/crossovers", GetForShowsAsync).RequireFeature(FeatureFlags.CrossoverAlerts);
    }

    private static async Task<Ok<List<CrossoverDto>>> GetUpcomingAsync(Crossovers crossovers, CancellationToken ct) =>
        TypedResults.Ok((await crossovers.UpcomingAsync(ct)).Select(ToDto).ToList());

    private static async Task<Ok<List<CrossoverDto>>> GetForShowsAsync(CrossoverShowsRequest request, Crossovers crossovers, CancellationToken ct)
    {
        var shows = request.ShowIds.Take(500).ToHashSet();
        return TypedResults.Ok((await crossovers.UpcomingAsync(ct)).Where(c => c.Parts.Any(p => shows.Contains(p.ShowId))).Select(ToDto).ToList());
    }

    private static CrossoverDto ToDto(Crossover c) => new(c.Id, c.Name, c.UniverseId, c.UniverseName, c.StartsAtUtc,
        c.Parts.Select(p => new CrossoverPartDto(p.ShowId, p.ShowTitle, p.PosterUrl, p.EpisodeId, p.SeasonNumber, p.EpisodeNumber, p.Title,
            p.AirsAtUtc)).ToList());
}
