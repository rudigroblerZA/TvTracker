using System.Net;
using Microsoft.Extensions.Caching.Hybrid;

namespace Ratings.API.Services;

/// <summary>
/// Who is in a show's cast, asked of Catalog.API, so a favourite-character vote can only name someone in it.
/// <list type="bullet">
/// <item><b>Kept for a while:</b> a cast is asked for once per show per <see cref="KeptFor"/>, not once per vote. A
/// new cast member can't be voted for until then.</item>
/// <item><b>A catalog that can't be reached is a gap, not a refusal:</b> the answer is "don't know" and the vote is
/// let through, as it was before there was a check. A show's page lists only its cast, so a stray id never shows.</item>
/// </list>
/// </summary>
public sealed class CatalogCast(HttpClient http, HybridCache cache, ILogger<CatalogCast> logger)
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(10);

    private static readonly HybridCacheEntryOptions Kept = new() { Expiration = KeptFor, LocalCacheExpiration = KeptFor };

    /// <summary>
    /// Whether <paramref name="personId"/> is in the cast of <paramref name="showId"/>; null when the catalog couldn't say.
    /// </summary>
    public async Task<bool?> HasAsync(Guid showId, Guid personId, CancellationToken ct) =>
        (await OfAsync(showId, ct))?.Contains(personId);

    /// <summary>
    /// The people in the cast of <paramref name="showId"/>; null when the catalog couldn't say.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>?> OfAsync(Guid showId, CancellationToken ct)
    {
        try
        {
            return await cache.GetOrCreateAsync($"cast:{showId}", async token =>
            {
                using var response = await http.GetAsync($"api/v1/shows/{showId}", token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Not a show the catalog has: nobody is in its cast.
                    return [];
                }
                response.EnsureSuccessStatusCode();
                var show = await response.Content.ReadFromJsonAsync<ShowCast>(token);
                return show?.Cast.Select(member => member.PersonId).ToArray() ?? [];
            }, Kept, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Couldn't ask the catalog for the cast of {ShowId}; it goes unchecked", showId);
            return null;
        }
    }

    /// <summary>
    /// The part of Catalog.API's show that is read here.
    /// </summary>
    private sealed record ShowCast(IReadOnlyList<CastMember> Cast);

    private sealed record CastMember(Guid PersonId);
}
