using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Catalog.API.Apis;

/// <param name="Mood">What the viewer is in the mood for, in their words ("something light and funny").</param>
public record MoodRequest(string Mood, IReadOnlyList<Guid> ShowIds);

/// <param name="Available">False while the embedding model isn't loaded; <paramref name="Fits"/> is empty then.</param>
/// <param name="Fits">How well each show fits the mood, 0-1 within this set (1 = the best fit of them), best first.
/// Shows not embedded yet are left out.</param>
public record MoodResponse(bool Available, IReadOnlyList<MoodFitDto> Fits);

/// <param name="Distance">Cosine distance between the mood and the show's text (0 = same meaning).</param>
public record MoodFitDto(Guid ShowId, double Fit, double Distance);

/// <summary>
/// Scores a set of shows against a mood with the semantic-search vectors, for Tonight's plan (which sends the user's
/// queue from Tracking). Public like search, and limited the same way, since each call embeds the mood.
/// </summary>
public static class MoodApi
{
    public const int MaxShows = 200;
    private const int MaxMoodLength = 200;

    public static void MapMoodApi(this RouteGroupBuilder api) =>
        api.MapPost("/shows/mood", ScoreAsync).RequireRateLimiting(SearchRateLimiting.Policy);

    private static async Task<Results<Ok<MoodResponse>, ValidationProblem>> ScoreAsync(
        MoodRequest request, SemanticSearch semantic, CatalogContext db, CancellationToken ct)
    {
        var mood = request.Mood?.Trim() ?? "";
        if (mood.Length is < 2 or > MaxMoodLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["mood"] = [$"Describe the mood in 2 to {MaxMoodLength} characters."] });
        }

        var ids = (request.ShowIds ?? []).Distinct().Take(MaxShows).ToList();
        var distances = ids.Count == 0 ? [] : await semantic.DistancesAsync(ids, db, mood, ct);
        if (distances is null)
        {
            return TypedResults.Ok(new MoodResponse(false, []));
        }

        // Relative within the set: absolute distances depend on the model and on how the mood is worded, but the
        // order and spread within the user's own shows is what the plan needs.
        var (best, worst) = distances.Count == 0 ? (0d, 0d) : (distances.Values.Min(), distances.Values.Max());
        var spread = worst - best;
        return TypedResults.Ok(new MoodResponse(true, distances
            .OrderBy(d => d.Value)
            .Select(d => new MoodFitDto(d.Key, spread < 1e-6 ? 1 : Math.Round(1 - (d.Value - best) / spread, 3), Math.Round(d.Value, 4)))
            .ToList()));
    }
}
