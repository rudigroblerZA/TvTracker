using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Keycloak settings shared by services and clients.
/// "Identity:Url" is the Keycloak base URL (the AppHost sets it); the realm and audience are fixed.
/// </summary>
public static class IdentitySettings
{
    public const string Realm = "tvtracker";
    public const string ApiAudience = "tvtracker-api";

    /// <summary>Keycloak as seen from the Android emulator; tokens issued there carry this issuer.</summary>
    public const string AndroidEmulatorAuthority = "http://10.0.2.2:8080/realms/" + Realm;

    public static string GetAuthority(IConfiguration configuration)
    {
        var url = configuration["Identity:Url"]
            ?? throw new InvalidOperationException("Missing configuration value 'Identity:Url' (the Keycloak base URL).");
        return $"{url.TrimEnd('/')}/realms/{Realm}";
    }
}

public static class AuthenticationExtensions
{
    /// <summary>
    /// Validates Keycloak access tokens (audience "tvtracker-api"). Claims keep their JWT names,
    /// so the user id is the "sub" claim.
    /// </summary>
    public static TBuilder AddDefaultAuthentication<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var authority = IdentitySettings.GetAuthority(builder.Configuration);

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = IdentitySettings.ApiAudience;
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidIssuers = [authority, IdentitySettings.AndroidEmulatorAuthority];
                options.TokenValidationParameters.NameClaimType = "name";
            });

        builder.Services.AddAuthorization();

        return builder;
    }

    /// <summary>The Keycloak user id ("sub" claim). Throws if the user isn't signed in.</summary>
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? throw new InvalidOperationException("The user has no 'sub' claim.");

    public static string? GetDisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username");

    public static string? GetEmail(this ClaimsPrincipal user) => user.FindFirstValue("email");
}
