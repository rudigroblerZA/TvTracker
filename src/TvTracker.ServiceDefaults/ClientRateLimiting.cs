using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Limits for <see cref="ClientRateLimiting"/>; bound from the "RateLimits" configuration section.
/// </summary>
public sealed class ClientRateLimitOptions
{
    /// <summary>
    /// Changes (POST, PUT, PATCH, DELETE) one user or client address may make in a burst. A history import's status
    /// step sends one call per show, so the burst is large; past it the app's client waits out each 429.
    /// </summary>
    public int WritesBurst { get; set; } = 120;

    public int WritesPerMinute { get; set; } = 300;

    /// <summary>
    /// Searches of people by name (<see cref="ClientRateLimiting.PeopleSearch"/>), as Catalog's show search.
    /// </summary>
    public int PeopleSearchBurst { get; set; } = 20;

    public int PeopleSearchPerMinute { get; set; } = 30;

    /// <summary>
    /// Data exports (<see cref="ClientRateLimiting.Export"/>) a user may ask for an hour: each one reads every service.
    /// </summary>
    public int ExportsPerHour { get; set; } = 3;
}

/// <summary>
/// Rate limits every service applies to people, told apart by <see cref="PartitionKey"/>: the signed-in user, else the
/// client address the mobile gateway or web app forwarded, else the calling service. Every change a person makes is
/// limited (a global limiter on writes); people search and the data export have tighter policies of their own. Calls
/// from other services (no token, nothing forwarded) aren't limited here: they're the app's own work, such as an
/// accepted correction reaching the sync worker. Over a limit the answer is 429 with Retry-After.
/// </summary>
public static class ClientRateLimiting
{
    public const string PeopleSearch = "people-search";
    public const string Export = "export";

    /// <summary>
    /// Who a request counts against, and whether that's a shared bucket for other services rather than a person. A
    /// Keycloak service account's token (client credentials: Mcp.Server's <c>tvtracker-mcp</c>, Ask's tools) has a
    /// <c>sub</c> too, but speaks for many people at once, so it counts as a service.
    /// </summary>
    public static (string Key, bool Shared) PartitionKey(HttpContext context)
    {
        if (IsServiceAccount(context.User))
        {
            return ($"service:{context.User.FindFirst("azp")?.Value ?? context.User.FindFirst("client_id")?.Value}", true);
        }
        if (context.User.FindFirst("sub")?.Value is { } userId)
        {
            return ($"user:{userId}", false);
        }

        // The mobile gateway (YARP) and the web app (each browser's address) set X-Forwarded-For. Use the last entry:
        // it's the one they added, while earlier entries come from the client and could be anything. Only internal
        // callers reach the services, so the header can't be set by someone else.
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        var lastHop = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (lastHop is not null && IPAddress.TryParse(lastHop, out var clientIp))
        {
            return ($"ip:{clientIp}", false);
        }

        return ($"service:{context.Connection.RemoteIpAddress}", true);
    }

    /// <summary>
    /// A token bucket refilled every 10 seconds, so a client that hits the limit waits seconds rather than a minute.
    /// </summary>
    public static RateLimitPartition<string> Bucket(string key, int burst, int perMinute) =>
        RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            TokensPerPeriod = Math.Max(1, perMinute / 6),
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            QueueLimit = 0,
            AutoReplenishment = true
        });

    /// <summary>
    /// Keycloak names a client's service account <c>service-account-&lt;client&gt;</c> and puts the client in
    /// <c>client_id</c> on client-credentials tokens; a person's token has neither.
    /// </summary>
    public static bool IsServiceAccount(System.Security.Claims.ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value?.StartsWith("service-account-", StringComparison.Ordinal) == true
        || user.FindFirst("client_id") is not null;

    /// <summary>
    /// What the write budget counts: a change (not a GET, HEAD or OPTIONS) to an endpoint without a policy of its own.
    /// Search, mood scoring, mixtapes and the history import's mapping are reads sent as POSTs, and their own policy
    /// (Catalog's search) already limits them; counting them as changes too would let a user's searches use up their
    /// budget for marking episodes watched.
    /// </summary>
    internal static bool CountsAsWrite(HttpContext context) =>
        !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method))
        && context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>() is null;

    public static IServiceCollection AddClientRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("RateLimits").Get<ClientRateLimitOptions>() ?? new ClientRateLimitOptions();
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

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var (key, shared) = PartitionKey(context);
                return !CountsAsWrite(context) || shared
                    ? RateLimitPartition.GetNoLimiter("unlimited")
                    : Bucket($"writes:{key}", options.WritesBurst, options.WritesPerMinute);
            });

            limiter.AddPolicy(PeopleSearch, context =>
            {
                var (key, shared) = PartitionKey(context);
                return shared ? RateLimitPartition.GetNoLimiter("unlimited") : Bucket(key, options.PeopleSearchBurst, options.PeopleSearchPerMinute);
            });

            limiter.AddPolicy(Export, context =>
            {
                var (key, _) = PartitionKey(context);
                return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Math.Max(1, options.ExportsPerHour),
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromHours(1) / Math.Max(1, options.ExportsPerHour),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });
        return services;
    }

    /// <summary>
    /// Authentication, authorization and then the rate limiter, in that order: the limiter partitions by the signed-in
    /// user, and with only <c>UseAuthentication</c> called, ASP.NET adds authorization before it.
    /// </summary>
    public static WebApplication UseDefaultAuthentication(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        return app;
    }
}
