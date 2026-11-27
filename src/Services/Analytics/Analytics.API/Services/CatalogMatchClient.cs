using System.Net.Http.Json;

namespace Analytics.API.Services;

/// <param name="Alike">How close the two sides are in meaning, 0-1; null when a side has too few shows the catalog can compare.</param>
/// <param name="Together">Shows neither side knows that sit between the two, best first.</param>
public sealed record CatalogComparison(double? Alike, int UsableFirst, int UsableSecond, IReadOnlyList<Guid> Together, bool Loose);

/// <summary>
/// Asks Catalog.API, which has the shows' vectors, how alike two sets of shows are and what sits between them
/// (its internal <c>shows/taste-match</c>). Only show ids and weights are sent: never whose they are.
/// </summary>
public sealed class CatalogMatchClient(HttpClient http)
{
    /// <summary>
    /// How many suggestions the catalog is asked for: more than are shown, since some may be missing from Analytics' copy.
    /// </summary>
    private const int Asked = 12;

    public async Task<CatalogComparison> CompareAsync((IReadOnlyDictionary<Guid, double> Loved, IReadOnlyCollection<Guid> Known) first,
        (IReadOnlyDictionary<Guid, double> Loved, IReadOnlyCollection<Guid> Known) second, CancellationToken ct)
    {
        static object Side((IReadOnlyDictionary<Guid, double> Loved, IReadOnlyCollection<Guid> Known) side) => new
        {
            Loved = side.Loved.Select(l => new { ShowId = l.Key, Weight = l.Value }),
            // The catalog caps what it reads; the loved shows are always among them.
            Known = side.Known.Take(MaxKnown)
        };

        using var response = await http.PostAsJsonAsync("api/v1/shows/taste-match",
            new { First = Side(first), Second = Side(second), TasteMatch.SeedsPerSide, MaxTogether = Asked }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogComparison>(ct)
            ?? throw new InvalidOperationException("The catalog answered a taste match with nothing.");
    }

    private const int MaxKnown = 5000;
}
