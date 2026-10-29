using System.Net;

namespace Research.API.Services;

public sealed record CatalogSeason(int Number, int EpisodeCount);

public sealed record CatalogCreative(Guid PersonId, string Name, IReadOnlyList<string> Roles);

/// <summary>The parts of Catalog.API's show detail research uses.</summary>
public sealed record CatalogShow(Guid Id, string Title, string? Overview, DateOnly? FirstAirDate, string? Network,
    IReadOnlyList<CatalogSeason> Seasons, IReadOnlyList<CatalogCreative>? Creatives, string? Source, string? ExternalId);

public sealed record CatalogEpisode(Guid Id, Guid ShowId, int SeasonNumber, int EpisodeNumber, string Title);

/// <summary>Catalog.API's facts about a popular show (<c>/api/v1/shows/facts</c>).</summary>
public sealed record CatalogShowFacts(Guid Id, string Title, string? PosterUrl, string Source, string ExternalId, string? Network,
    DateOnly? FirstAirDate, TvTracker.IntegrationEvents.ShowStatus Status, double Popularity, int AiredEpisodes);

/// <summary>Reads shows from Catalog.API, as any client does.</summary>
public sealed class CatalogReader(HttpClient http)
{
    /// <summary>The most popular shows, most popular first, with the facts the research queue checks.</summary>
    public async Task<List<CatalogShowFacts>> GetPopularFactsAsync(int limit, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<CatalogShowFacts>>($"api/v1/shows/facts?limit={limit}", ct) ?? [];

    public async Task<CatalogEpisode?> GetEpisodeAsync(Guid episodeId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/v1/episodes/{episodeId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogEpisode>(ct);
    }

    public async Task<CatalogShow?> GetShowAsync(Guid showId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/v1/shows/{showId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogShow>(ct);
    }
}

public enum OverrideOutcome { Applied, Removed, NotInCatalog, Invalid, Unavailable }

/// <summary>
/// Sends saved research's catalog fields to the sync worker's overrides (internal, as for Ratings' accepted
/// corrections). Each (show, field) has one override id, so saving again replaces the value rather than stacking.
/// </summary>
public sealed class CatalogOverrides(HttpClient http, ILogger<CatalogOverrides> logger)
{
    public static Guid OverrideId(Guid showId, string field)
    {
        // A stable id per show and field: the first 16 bytes of SHA-256 over both, with version 8 (custom) bits set.
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"research:{showId}:{field}"));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public async Task<(OverrideOutcome Outcome, string? Detail)> ApplyAsync(Guid showId, string field, string value, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/v1/overrides",
                new { id = OverrideId(showId, field), showId, episodeId = (Guid?)null, field, value }, ct);
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
            logger.LogWarning(ex, "Couldn't reach the catalog sync worker to apply {Field} for {Show}", field, showId);
            return (OverrideOutcome.Unavailable, null);
        }
    }

    /// <summary>Lets the provider's value back (on the show's next refresh, due at once).</summary>
    public async Task<OverrideOutcome> RemoveAsync(Guid showId, string field, CancellationToken ct)
    {
        try
        {
            using var response = await http.DeleteAsync($"api/v1/overrides/{OverrideId(showId, field)}", ct);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound ? OverrideOutcome.Removed : OverrideOutcome.Unavailable;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Couldn't reach the catalog sync worker to remove {Field} for {Show}", field, showId);
            return OverrideOutcome.Unavailable;
        }
    }
}
