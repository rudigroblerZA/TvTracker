using System.Net;

namespace Ratings.API.Services;

public enum OverrideOutcome { Applied, NotInCatalog, Invalid, Unavailable }

/// <summary>The catalog sync worker's overrides API, which applies accepted corrections to the catalog.</summary>
public sealed class CatalogSyncClient(HttpClient http, ILogger<CatalogSyncClient> logger)
{
    public async Task<(OverrideOutcome Outcome, string? Detail)> ApplyAsync(Guid id, Guid showId, Guid? episodeId, string field, string value,
        CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/v1/overrides", new { id, showId, episodeId, field, value }, ct);
            return response.StatusCode switch
            {
                _ when response.IsSuccessStatusCode => (OverrideOutcome.Applied, null),
                HttpStatusCode.NotFound => (OverrideOutcome.NotInCatalog, null),
                HttpStatusCode.BadRequest => (OverrideOutcome.Invalid, await response.Content.ReadAsStringAsync(ct)),
                _ => (OverrideOutcome.Unavailable, $"{(int)response.StatusCode}")
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Couldn't reach the catalog sync worker to apply correction {Id}", id);
            return (OverrideOutcome.Unavailable, null);
        }
    }

    /// <summary>True when the override is gone (or never existed).</summary>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        try
        {
            using var response = await http.DeleteAsync($"api/v1/overrides/{id}", ct);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Couldn't reach the catalog sync worker to revert correction {Id}", id);
            return false;
        }
    }
}
