using System.Net.Http.Json;

namespace Analytics.API.Services;

/// <param name="Alike">How close the two sides are in meaning, 0-1; null when a side has too few shows the catalog can compare.</param>
/// <param name="SeedsFirst">The first side's shows the suggestions were seeded with.</param>
/// <param name="Together">Shows that sit between the two, best first; neither side's compared shows are among them.</param>
public sealed record CatalogComparison(double? Alike, IReadOnlyList<Guid>? SeedsFirst, IReadOnlyList<Guid>? SeedsSecond, IReadOnlyList<Guid>? Together,
    bool Loose);

/// <summary>
/// Asks Catalog.API, which has the shows' vectors, how alike two sets of shows are and what sits between them
/// (its internal <c>shows/taste-match</c>). Only show ids and weights are sent: never whose they are. Each side is at
/// most <see cref="TasteMatch.MaxScored"/> shows, within what the catalog accepts (its <c>TasteRules.MaxLoved</c>).
/// </summary>
public sealed class CatalogMatchClient(HttpClient http, ILogger<CatalogMatchClient> logger)
{
    /// <returns>Null when the catalog couldn't answer: down, slow, or (mid-deploy) a version that speaks this differently.</returns>
    public async Task<CatalogComparison?> TryCompareAsync(IReadOnlyDictionary<Guid, double> first, IReadOnlyDictionary<Guid, double> second,
        CancellationToken ct)
    {
        try
        {
            return await CompareAsync(first, second, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The catalog couldn't compare two tastes for a match");
            return null;
        }
    }

    private async Task<CatalogComparison> CompareAsync(IReadOnlyDictionary<Guid, double> first, IReadOnlyDictionary<Guid, double> second,
        CancellationToken ct)
    {
        static IEnumerable<object> Side(IReadOnlyDictionary<Guid, double> side) => side.Select(l => new { ShowId = l.Key, Weight = l.Value });

        using var response = await http.PostAsJsonAsync("api/v1/shows/taste-match", new { First = Side(first), Second = Side(second) }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogComparison>(ct)
            ?? throw new InvalidOperationException("The catalog answered a taste match with nothing.");
    }
}
