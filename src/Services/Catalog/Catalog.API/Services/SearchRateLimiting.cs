using System.Threading.RateLimiting;

namespace Catalog.API.Services;

/// <summary>
/// Limits for <see cref="SearchRateLimiting"/>; bound from the "SearchRateLimit" configuration section.
/// </summary>
public sealed class SearchRateLimitOptions
{
    /// <summary>
    /// Searches a signed-in user, or one client IP (from the mobile gateway or the web app), may make in a burst.
    /// </summary>
    public int PerClientBurst { get; set; } = 20;

    /// <summary>
    /// Searches per minute a signed-in user or client IP gets back after a burst.
    /// </summary>
    public int PerClientPerMinute { get; set; } = 30;

    /// <summary>
    /// Burst for callers that send neither a token nor X-Forwarded-For: other services rather than people (the web app
    /// forwards each browser's address). Shared by those callers, so it's larger.
    /// </summary>
    public int SharedBurst { get; set; } = 200;

    public int SharedPerMinute { get; set; } = 300;

    /// <summary>
    /// New search terms per minute, across all callers, that may be sent to the sync worker for import.
    /// </summary>
    public int ImportsPerMinute { get; set; } = 30;
}

/// <summary>
/// Rate limiting for the search endpoint (and Catalog's other costly reads), with callers told apart as every limit is
/// (<see cref="ClientRateLimiting.PartitionKey"/>): over the limit, 429 with Retry-After. Services share a larger bucket.
/// </summary>
public static class SearchRateLimiting
{
    public const string Policy = "search";

    public static IServiceCollection AddSearchRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("SearchRateLimit").Get<SearchRateLimitOptions>() ?? new SearchRateLimitOptions();
        services.AddSingleton(new SearchImportLimiter(options.ImportsPerMinute));
        services.AddRateLimiter(limiter => limiter.AddPolicy(Policy, context =>
        {
            var (key, shared) = ClientRateLimiting.PartitionKey(context);
            return shared
                ? ClientRateLimiting.Bucket(key, options.SharedBurst, options.SharedPerMinute)
                : ClientRateLimiting.Bucket(key, options.PerClientBurst, options.PerClientPerMinute);
        }));
        return services;
    }
}

/// <summary>
/// Caps how many new search terms per minute go to the sync worker for import, across all callers, so the
/// TVmaze imports (provider calls, writes and catalog events) stay bounded however callers are partitioned.
/// Over the cap, search answers from the catalog as it is.
/// </summary>
public sealed class SearchImportLimiter(int importsPerMinute) : IDisposable
{
    private readonly TokenBucketRateLimiter _limiter = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = Math.Max(1, importsPerMinute),
        TokensPerPeriod = Math.Max(1, importsPerMinute / 6),
        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
        QueueLimit = 0,
        AutoReplenishment = true
    });

    public bool TryAcquire()
    {
        using var lease = _limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
