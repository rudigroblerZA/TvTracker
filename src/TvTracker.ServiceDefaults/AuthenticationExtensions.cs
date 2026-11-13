using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Keycloak settings shared by services and clients.
/// "Identity:Url" is the Keycloak base URL (the AppHost sets it); the realm and audience are fixed.
/// </summary>
public static class IdentitySettings
{
    public const string Realm = "tvtracker";
    public const string ApiAudience = "tvtracker-api";

    /// <summary>
    /// The Keycloak client services sign in as to call another service's internal API (the sync worker's imports,
    /// replay and overrides): <see cref="AuthenticationExtensions.InternalPolicy"/>.
    /// </summary>
    public const string InternalClient = "tvtracker-internal";

    /// <summary>
    /// Keycloak as seen from the Android emulator; tokens issued there carry this issuer. Accepted in Development only:
    /// a development address has no place in production validation.
    /// </summary>
    public const string AndroidEmulatorAuthority = "http://10.0.2.2:8080/realms/" + Realm;

    public static string GetAuthority(IConfiguration configuration)
    {
        var url = configuration["Identity:Url"]
            ?? throw new InvalidOperationException("Missing configuration value 'Identity:Url' (the Keycloak base URL).");
        return $"{url.TrimEnd('/')}/realms/{Realm}";
    }

    /// <summary>
    /// The realm as people's browsers and phones reach it (<c>Identity:PublicUrl</c>, the site's public address when
    /// sign-in goes through it), or null. A token someone got there names it as its issuer, where one the web app got
    /// for them over the private network names <see cref="GetAuthority"/>; both are this realm's.
    /// </summary>
    public static string? PublicAuthority(IConfiguration configuration) =>
        configuration["Identity:PublicUrl"] is { Length: > 0 } url ? $"{url.TrimEnd('/')}/realms/{Realm}" : null;

    /// <summary>
    /// Whether Keycloak's metadata must come over HTTPS: yes outside development, unless
    /// <c>Identity:RequireHttpsMetadata</c> says otherwise, for a deployment where the services reach Keycloak by plain
    /// HTTP on a private network (one machine's Docker network) and only the front door has a certificate.
    /// </summary>
    public static bool RequireHttpsMetadata(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue<bool?>("Identity:RequireHttpsMetadata") ?? !environment.IsDevelopment();
}

public static class AuthenticationExtensions
{
    /// <summary>
    /// Internal APIs, which only other services may call: a valid token from a client in <c>Internal:Clients</c>
    /// (<see cref="IdentitySettings.InternalClient"/> by default), whoever forwards it. A person's token is refused (403)
    /// even when it's valid, and no token at all is 401.
    /// </summary>
    public const string InternalPolicy = "internal";

    /// <summary>
    /// Validates Keycloak access tokens (audience "tvtracker-api"). Claims keep their JWT names,
    /// so the user id is the "sub" claim. Also registers the rate limits every service applies to people
    /// (<see cref="ClientRateLimiting"/>); <see cref="ClientRateLimiting.UseDefaultAuthentication"/> turns both on.
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
                options.RequireHttpsMetadata = IdentitySettings.RequireHttpsMetadata(builder.Configuration, builder.Environment);
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidIssuers =
                [
                    authority,
                    .. IdentitySettings.PublicAuthority(builder.Configuration) is { } publicAuthority ? [publicAuthority] : Array.Empty<string>(),
                    .. builder.Environment.IsDevelopment() ? [IdentitySettings.AndroidEmulatorAuthority] : Array.Empty<string>()
                ];
                options.TokenValidationParameters.NameClaimType = "name";
            });

        var internalClients = builder.Configuration.GetSection("Internal:Clients").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : [IdentitySettings.InternalClient];
        builder.Services.AddAuthorization(options => options.AddPolicy(InternalPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => IsInternalCaller(context.User, internalClients))));
        builder.Services.AddClientRateLimiting(builder.Configuration);

        return builder;
    }

    /// <summary>
    /// A service signed in as one of the internal clients: a client-credentials token (a service account, so never a
    /// person's) whose client is listed.
    /// </summary>
    public static bool IsInternalCaller(ClaimsPrincipal user, IReadOnlyCollection<string> internalClients) =>
        ClientRateLimiting.IsServiceAccount(user)
        && (user.FindFirstValue("azp") ?? user.FindFirstValue("client_id")) is { } client
        && internalClients.Contains(client, StringComparer.Ordinal);

    /// <summary>
    /// The Keycloak user id ("sub" claim). Throws if the user isn't signed in.
    /// </summary>
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? throw new InvalidOperationException("The user has no 'sub' claim.");

    public static string? GetDisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username");

    public static string? GetEmail(this ClaimsPrincipal user) => user.FindFirstValue("email");
}
