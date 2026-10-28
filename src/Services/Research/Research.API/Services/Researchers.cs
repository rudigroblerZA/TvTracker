using System.Security.Claims;

namespace Research.API.Services;

/// <summary>Who may research: the moderators, the same people as Ratings.API's (<c>Admin:Users</c> and <c>Moderation:Users</c>).</summary>
public sealed class Researchers(IConfiguration config)
{
    public bool Allowed(ClaimsPrincipal user) =>
        user.FindFirstValue("preferred_username") is { } name
        && (config.GetSection("Admin:Users").Get<string[]>() ?? []).Concat(config.GetSection("Moderation:Users").Get<string[]>() ?? [])
            .Contains(name, StringComparer.OrdinalIgnoreCase);
}
