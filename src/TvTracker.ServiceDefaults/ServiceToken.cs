using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TvTracker;

/// <summary>
/// A service's own access token, for calls made as the service rather than for a user: the callers of the sync
/// worker's internal APIs (<c>tvtracker-internal</c>, which the sync worker requires; <see cref="Microsoft.Extensions.Hosting.AuthenticationExtensions.InternalPolicy"/>). It signs in with
/// the client-credentials grant as <c>{section}:ClientId</c> / <c>{section}:ClientSecret</c>; the token carries the
/// services' audience and no person, and is kept until 80% of its lifetime.
/// <para>
/// Keycloak imports the realm only on its first start. A realm made before a client existed gets it from Identity.API on
/// start (<c>Keycloak:ServiceClients</c>), or, in development, here, when the service has the master realm's admin
/// (<c>Keycloak:AdminUsername</c> / <c>AdminPassword</c>; no service is given it today).
/// </para>
/// </summary>
public sealed class ServiceToken(HttpClient http, IConfiguration config, IHostEnvironment environment, ILogger<ServiceToken> logger,
    string section, string defaultClientId)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? token;
    private DateTimeOffset refreshAt;

    private string BaseUrl => (config["Identity:Url"] ?? throw new InvalidOperationException("Missing configuration value 'Identity:Url'.")).TrimEnd('/');
    public string ClientId => config[$"{section}:ClientId"] ?? defaultClientId;
    private string ClientSecret => config[$"{section}:ClientSecret"]
        ?? throw new InvalidOperationException($"Missing configuration value '{section}:ClientSecret'.");

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (token is not null && DateTimeOffset.UtcNow < refreshAt)
        {
            return token;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (token is not null && DateTimeOffset.UtcNow < refreshAt)
            {
                return token;
            }

            var response = await RequestAsync(ct);
            if (response is null && environment.IsDevelopment() && await EnsureClientAsync(ct))
            {
                response = await RequestAsync(ct);
            }
            if (response is null)
            {
                throw new InvalidOperationException($"Keycloak refused the client '{ClientId}'. Check {section}:ClientSecret, or create the client.");
            }

            token = response.AccessToken;
            refreshAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn * 0.8);
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Null when Keycloak doesn't know the client or its secret (401, or 400 invalid_client).
    /// </summary>
    private async Task<TokenResponse?> RequestAsync(CancellationToken ct)
    {
        using var response = await http.PostAsync($"{BaseUrl}/realms/{IdentitySettings.Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret
            }), ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
    }

    /// <summary>
    /// Creates the client unless it exists, when this service has the master realm's admin. True when it created it.
    /// </summary>
    private async Task<bool> EnsureClientAsync(CancellationToken ct)
    {
        if (config["Keycloak:AdminUsername"] is not { Length: > 0 } username || config["Keycloak:AdminPassword"] is not { Length: > 0 } password)
        {
            return false;
        }

        using var signIn = await http.PostAsync($"{BaseUrl}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = username, ["password"] = password }), ct);
        signIn.EnsureSuccessStatusCode();
        var admin = (await signIn.Content.ReadFromJsonAsync<TokenResponse>(ct))!.AccessToken;

        var clients = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/clients";
        using var find = new HttpRequestMessage(HttpMethod.Get, $"{clients}?clientId={Uri.EscapeDataString(ClientId)}");
        find.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var found = await http.SendAsync(find, ct);
        found.EnsureSuccessStatusCode();
        if ((await found.Content.ReadFromJsonAsync<List<object>>(ct))!.Count > 0)
        {
            return false;
        }

        using var create = new HttpRequestMessage(HttpMethod.Post, clients)
        {
            Content = JsonContent.Create(ServiceClients.Definition(ClientId, ClientId, ClientSecret))
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var created = await http.SendAsync(create, ct);
        if (created.StatusCode == HttpStatusCode.Conflict)
        {
            return true;
        }
        created.EnsureSuccessStatusCode();
        logger.LogInformation("Created the Keycloak client {ClientId}", ClientId);
        return true;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>
/// Puts a <see cref="ServiceToken"/> on each request of the HttpClient it's added to.
/// </summary>
public sealed class ServiceTokenHandler(ServiceToken token) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.GetAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Service clients in Keycloak: confidential, client credentials only, with the same audience mapper as the app's clients,
/// so the services accept their tokens.
/// </summary>
public static class ServiceClients
{
    public static object Definition(string clientId, string name, string secret) => new
    {
        clientId,
        name,
        enabled = true,
        protocol = "openid-connect",
        publicClient = false,
        secret,
        standardFlowEnabled = false,
        directAccessGrantsEnabled = false,
        serviceAccountsEnabled = true,
        protocolMappers = new[]
        {
            new
            {
                name = "tvtracker-api-audience",
                protocol = "openid-connect",
                protocolMapper = "oidc-audience-mapper",
                config = new Dictionary<string, string>
                {
                    ["included.custom.audience"] = IdentitySettings.ApiAudience,
                    ["id.token.claim"] = "false",
                    ["access.token.claim"] = "true"
                }
            }
        }
    };

    /// <summary>
    /// Registers the service's token (one per process) and its handler; add the handler to each client that calls as the
    /// service: <c>.AddHttpMessageHandler&lt;ServiceTokenHandler&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddServiceToken(this IServiceCollection services, string section, string defaultClientId)
    {
        services.AddHttpClient(nameof(ServiceToken));
        services.AddSingleton(sp => new ServiceToken(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ServiceToken)),
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<ILogger<ServiceToken>>(),
            section, defaultClientId));
        services.AddTransient<ServiceTokenHandler>();
        return services;
    }
}
