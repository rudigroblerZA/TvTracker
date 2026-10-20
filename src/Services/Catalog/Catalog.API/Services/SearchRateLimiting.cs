using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Catalog.API.Services;

/// <summary>Limits for <see cref="SearchRateLimiting"/>; bound from the "SearchRateLimit" configuration section.</summary>
public sealed class SearchRateLimitOptions
{
    /// <summary>Searches a signed-in user, or one client IP behind the mobile gateway, may make in a burst.</summary>
    public int PerClientBurst { get; set; } = 20;

    /// <summary>Searches per minute a signed-in user or client IP gets back after a burst.</summary>
    public int PerClientPerMinute { get; set; } = 30;

    /// <summary>
    /// Burst for callers that send neither a token nor X-Forwarded-For: the web app, on behalf of every anonymous
    /// web visitor (it calls from its own server, so they can't be told apart). Shared, so it's larger.
    /// </summary>
    public int SharedBurst { get; set; } = 200;

    public int SharedPerMinute { get; set; } = 300;

    /// <summary>New search terms per minute, across all callers, that may be sent to the sync worker for import.</summary>
    public int ImportsPerMinute { get; set; } = 30;
}

/// <summary>
/// Rate limiting for the search endpoint. Callers are told apart, in order, by the signed-in user (a validated
/// Keycloak token), the client IP the mobile gateway adds as X-Forwarded-For, or the calling service's address.
/// Over the limit, search returns 429 with Retry-After.
/// </summary>
public static class SearchRateLimiting
{
    public const string Policy = "search";

    public static IServiceCollection AddSearchRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("SearchRateLimit").Get<SearchRateLimitOptions>() ?? new SearchRateLimitOptions();
        services.AddSingleton(new SearchImportLimiter(options.ImportsPerMinute));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };

            limiter.AddPolicy(Policy, context =>
            {
                var (key, shared) = PartitionKey(context);
                var burst = shared ? options.SharedBurst : options.PerClientBurst;
                var perMinute = shared ? options.SharedPerMinute : options.PerClientPerMinute;

                return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = burst,
                    // Refill every 10 seconds, so a client that hits the limit waits seconds rather than a minute.
                    TokensPerPeriod = Math.Max(1, perMinute / 6),
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

        return services;
    }

    private static (string Key, bool Shared) PartitionKey(HttpContext context)
    {
        if (context.User.FindFirst("sub")?.Value is { } userId)
        {
            return ($"user:{userId}", false);
        }

        // The mobile gateway (YARP) sets X-Forwarded-For. Use the last entry: it's the one the gateway added,
        // while earlier entries come from the client and could be anything.
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        var lastHop = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (lastHop is not null && IPAddress.TryParse(lastHop, out var clientIp))
        {
            return ($"ip:{clientIp}", false);
        }

        return ($"service:{context.Connection.RemoteIpAddress}", true);
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
