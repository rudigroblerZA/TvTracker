using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;

namespace WebApp.Services;

public record StoredTokens(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Keeps each signed-in user's Keycloak tokens on the server (in Redis), keyed by user id,
/// and refreshes the access token when it is about to expire. Tokens never reach the browser.
/// </summary>
public sealed class ServerTokenStore(
    IDistributedCache cache,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ServerTokenStore> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    public async Task SaveAsync(string userId, StoredTokens tokens, CancellationToken ct = default)
    {
        await cache.SetStringAsync(Key(userId), JsonSerializer.Serialize(tokens), new DistributedCacheEntryOptions
        {
            // Matches the Keycloak SSO session; after that the user signs in again.
            SlidingExpiration = TimeSpan.FromDays(7)
        }, ct);
    }

    public async Task<StoredTokens?> GetAsync(string userId, CancellationToken ct = default)
    {
        var json = await cache.GetStringAsync(Key(userId), ct);
        return json is null ? null : JsonSerializer.Deserialize<StoredTokens>(json);
    }

    public Task RemoveAsync(string userId, CancellationToken ct = default) => cache.RemoveAsync(Key(userId), ct);

    /// <summary>Returns a non-expired access token, refreshing it if needed; null if the session can't be renewed.</summary>
    public async Task<string?> GetValidAccessTokenAsync(string userId, CancellationToken ct = default)
    {
        var tokens = await GetAsync(userId, ct);
        if (tokens is null)
        {
            return null;
        }
        if (tokens.ExpiresAtUtc - RefreshMargin > DateTimeOffset.UtcNow)
        {
            return tokens.AccessToken;
        }

        var gate = Locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another request may have refreshed while we waited.
            tokens = await GetAsync(userId, ct);
            if (tokens is null)
            {
                return null;
            }
            if (tokens.ExpiresAtUtc - RefreshMargin > DateTimeOffset.UtcNow)
            {
                return tokens.AccessToken;
            }

            var refreshed = await RefreshAsync(tokens, ct);
            if (refreshed is null)
            {
                await RemoveAsync(userId, ct);
                return null;
            }

            await SaveAsync(userId, refreshed, ct);
            return refreshed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<StoredTokens?> RefreshAsync(StoredTokens tokens, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            return null;
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
            ["client_id"] = configuration["Identity:WebClientId"] ?? "tvtracker-web",
            ["client_secret"] = configuration["Identity:WebClientSecret"] ?? ""
        });

        var authority = IdentitySettings.GetAuthority(configuration);
        using var response = await httpClientFactory.CreateClient("keycloak")
            .PostAsync($"{authority}/protocol/openid-connect/token", form, ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Token refresh failed with {Status}; the user must sign in again", response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (body?.AccessToken is null)
        {
            return null;
        }

        return new StoredTokens(
            body.AccessToken,
            body.RefreshToken ?? tokens.RefreshToken,
            body.IdToken ?? tokens.IdToken,
            DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn));
    }

    private static string Key(string userId) => $"webapp:tokens:{userId}";

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
