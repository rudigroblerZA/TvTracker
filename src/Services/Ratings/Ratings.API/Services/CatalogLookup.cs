using System.Net;
using Microsoft.Extensions.Caching.Hybrid;

namespace Ratings.API.Services;

/// <summary>
/// What Ratings asks Catalog.API about a favourite-character vote: who is in a show's cast, so a vote can only name
/// someone in it, and which show an episode belongs to, for a watch that doesn't say.
/// <list type="bullet">
/// <item><b>Kept for a while:</b> a cast is asked for once per show per <see cref="KeptFor"/>, not once per vote. A
/// new cast member can't be voted for until then.</item>
/// <item><b>A catalog that can't be reached is a gap, not a refusal:</b> the answer is "don't know" and the vote is
/// let through, as it was before there was a check. A show's page lists only its cast, so a stray id never shows.</item>
/// </list>
/// </summary>
public sealed class CatalogLookup(HttpClient http, HybridCache cache, ILogger<CatalogLookup> logger)
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(10);

    private static readonly HybridCacheEntryOptions Kept = new() { Expiration = KeptFor, LocalCacheExpiration = KeptFor };

    /// <summary>
    /// Whether <paramref name="personId"/> is in the cast of <paramref name="showId"/>; null when the catalog couldn't say.
    /// </summary>
    public async Task<bool?> InCastAsync(Guid showId, Guid personId, CancellationToken ct) =>
        (await CastOfAsync(showId, ct))?.Contains(personId);

    /// <summary>
    /// The people in the cast of <paramref name="showId"/>; null when the catalog couldn't say.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>?> CastOfAsync(Guid showId, CancellationToken ct)
    {
        try
        {
            // Not a show the catalog has: nobody is in its cast.
            return await cache.GetOrCreateAsync($"cast:{showId}", async token =>
                (await GetAsync<ShowCast>($"api/v1/shows/{showId}", token))?.Cast.Select(member => member.PersonId).ToArray() ?? [],
                Kept, cancellationToken: ct);
        }
        catch (Exception ex) when (CouldNotSay(ex, ct))
        {
            logger.LogWarning(ex, "Couldn't ask the catalog for the cast of {ShowId}; it goes unchecked", showId);
            return null;
        }
    }

    /// <summary>
    /// The show <paramref name="episodeId"/> belongs to; null when the catalog doesn't have the episode or couldn't say.
    /// Asked only for a watch that doesn't name its show, so it isn't kept.
    /// </summary>
    public async Task<Guid?> ShowOfAsync(Guid episodeId, CancellationToken ct)
    {
        try
        {
            var showId = (await GetAsync<EpisodeShow>($"api/v1/episodes/{episodeId}", ct))?.ShowId;
            return showId == Guid.Empty ? null : showId;
        }
        catch (Exception ex) when (CouldNotSay(ex, ct))
        {
            logger.LogWarning(ex, "Couldn't ask the catalog which show episode {EpisodeId} belongs to", episodeId);
            return null;
        }
    }

    /// <summary>
    /// What the catalog has at <paramref name="path"/>; null when it has nothing there. Throws when it can't be asked.
    /// </summary>
    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var response = await http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is the catalog failing to answer, and not the caller giving up.
    /// </summary>
    private static bool CouldNotSay(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested;

    /// <summary>
    /// The part of Catalog.API's show that is read here.
    /// </summary>
    private sealed record ShowCast(IReadOnlyList<CastMember> Cast);

    private sealed record CastMember(Guid PersonId);

    private sealed record EpisodeShow(Guid ShowId);
}
