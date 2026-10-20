using Microsoft.Extensions.Caching.Hybrid;

namespace Tracking.API.Services;

/// <summary>Per-user cache entries (Up Next) and their invalidation.</summary>
public sealed class UserCache(HybridCache cache)
{
    private static readonly HybridCacheEntryOptions UpNextOptions = new()
    {
        // Up Next depends on the clock (episodes air), so entries stay short-lived.
        Expiration = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };

    public ValueTask<T> GetUpNextAsync<T>(string userId, Func<CancellationToken, ValueTask<T>> factory, CancellationToken ct) =>
        cache.GetOrCreateAsync($"tracking:upnext:{userId}", factory, UpNextOptions, [UserTag(userId)], ct);

    public ValueTask InvalidateAsync(string userId, CancellationToken ct) => cache.RemoveByTagAsync(UserTag(userId), ct);

    private static string UserTag(string userId) => $"tracking:user:{userId}";
}
