using System.Security.Claims;
using Microsoft.Extensions.Configuration;

namespace TvTracker;

/// <summary>
/// The app's admins: the Keycloak usernames in <c>Admin:Users</c>, which the AppHost gives to each service with an
/// admin page. One rule, so the services can't disagree about who is one.
/// </summary>
public static class AppAdmins
{
    public static bool Is(ClaimsPrincipal user, IConfiguration config) =>
        user.FindFirstValue("preferred_username") is { } name
        && (config.GetSection("Admin:Users").Get<string[]>() ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);
}
