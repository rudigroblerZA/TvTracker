using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TvTracker;

/// <summary>
/// The app's admins: the Keycloak usernames in <c>Admin:Users</c>, which the AppHost gives to each service with an
/// admin page. One rule for Identity, Catalog and Financials, so they can't disagree about who is one.
/// </summary>
public static class AppAdmins
{
    public static bool Is(ClaimsPrincipal user, IConfiguration config) =>
        user.FindFirstValue("preferred_username") is { } name
        && (config.GetSection("Admin:Users").Get<string[]>() ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Answers 404 to anyone but an admin, as if the endpoint didn't exist.
    /// </summary>
    public static TBuilder RequireAppAdmin<TBuilder>(this TBuilder endpoints) where TBuilder : IEndpointConventionBuilder =>
        endpoints.AddEndpointFilter(async (context, next) =>
            Is(context.HttpContext.User, context.HttpContext.RequestServices.GetRequiredService<IConfiguration>())
                ? await next(context)
                : TypedResults.NotFound());
}
