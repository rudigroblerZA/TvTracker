using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace Mcp.Server.Services;

/// <summary>
/// The MCP server's own access token, for Analytics.API (every endpoint there needs sign-in). It signs in as the
/// confidential client <c>Mcp:ClientId</c> with the client-credentials grant; the token carries the services' audience
/// and no user, so only endpoints that don't need a user answer it.
/// <para>
/// Keycloak imports the realm only on its first start, so in development a realm made before this client existed gets
/// it here, through the master realm's admin API (<c>Keycloak:AdminUsername</c> / <c>Keycloak:AdminPassword</c>, set by
/// the AppHost), as <see cref="ClientDefinition"/>.
/// </para>
/// </summary>
public sealed class ServiceToken(IHttpClientFactory httpFactory, IConfiguration config, IHostEnvironment environment, ILogger<ServiceToken> logger)
{
    private readonly HttpClient http = httpFactory.CreateClient(nameof(ServiceToken));
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? token;
    private DateTimeOffset refreshAt;

    private string BaseUrl => (config["Identity:Url"] ?? throw new InvalidOperationException("Missing configuration value 'Identity:Url'.")).TrimEnd('/');
    private string ClientId => config["Mcp:ClientId"] ?? "tvtracker-mcp";
    private string ClientSecret => config["Mcp:ClientSecret"] ?? throw new InvalidOperationException("Missing configuration value 'Mcp:ClientSecret'.");

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
                throw new InvalidOperationException($"Keycloak refused the client '{ClientId}'. Check Mcp:ClientSecret, or create the client.");
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

    /// <summary>Null when Keycloak doesn't know the client or its secret (401, or 400 invalid_client).</summary>
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

    /// <summary>Creates the client unless it exists. True when it created it.</summary>
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

        using var create = new HttpRequestMessage(HttpMethod.Post, clients) { Content = JsonContent.Create(ClientDefinition(ClientId, ClientSecret)) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var created = await http.SendAsync(create, ct);
        created.EnsureSuccessStatusCode();
        logger.LogInformation("Created the Keycloak client {ClientId} for the MCP server", ClientId);
        return true;
    }

    /// <summary>Confidential, client credentials only, with the same audience mapper as the app's clients.</summary>
    private static object ClientDefinition(string clientId, string secret) => new
    {
        clientId,
        name = "MCP server (internal queries)",
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

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>Adds the service token to Analytics.API calls.</summary>
public sealed class ServiceTokenHandler(ServiceToken token) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.GetAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}
