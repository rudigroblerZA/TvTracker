using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Identity.API.Services;

/// <summary>
/// Calls the Keycloak admin API with the "tvtracker-identity-admin" service account
/// (client credentials; it has the realm-management manage-users role), or, for setting client secrets
/// (<see cref="RealmSecretsSeeder"/>), with a master-realm admin token from <see cref="GetMasterAdminTokenAsync"/>.
/// </summary>
public sealed class KeycloakAdminClient(HttpClient httpClient, IConfiguration configuration, ILogger<KeycloakAdminClient> logger)
{
    private string BaseUrl => (configuration["Identity:Url"] ?? throw new InvalidOperationException("Missing Identity:Url")).TrimEnd('/');

    private string Users => $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/users";

    public async Task DeleteUserAsync(string userId, CancellationToken cancellationToken)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        var (status, _) = await SendAsync<JsonNode>(HttpMethod.Delete, $"{Users}/{Uri.EscapeDataString(userId)}", token, null, cancellationToken,
            HttpStatusCode.NotFound);
        if (status == HttpStatusCode.NotFound)
        {
            logger.LogInformation("Keycloak user {UserId} was already deleted", userId);
        }
    }

    /// <summary>
    /// Makes sure a user exists with this password: creates them when missing (true), else resets their password, so
    /// the configured value is the one that holds (false).
    /// </summary>
    public async Task<bool> EnsureUserAsync(string username, string password, string email, string firstName, string lastName,
        CancellationToken cancellationToken)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        if (await FindUserIdAsync(token, username, cancellationToken) is { } id)
        {
            await SendAsync<JsonNode>(HttpMethod.Put, $"{Users}/{id}/reset-password", token,
                new JsonObject { ["type"] = "password", ["value"] = password, ["temporary"] = false }, cancellationToken);
            return false;
        }

        var (status, _) = await SendAsync<JsonNode>(HttpMethod.Post, Users, token, new JsonObject
        {
            ["username"] = username,
            ["email"] = email,
            ["firstName"] = firstName,
            ["lastName"] = lastName,
            ["enabled"] = true,
            ["emailVerified"] = true,
            ["credentials"] = new JsonArray(new JsonObject { ["type"] = "password", ["value"] = password, ["temporary"] = false })
        }, cancellationToken, HttpStatusCode.Conflict);
        return status != HttpStatusCode.Conflict;
    }

    /// <summary>
    /// A token for the master realm's admin (password grant through admin-cli), which can manage clients.
    /// </summary>
    public Task<string> GetMasterAdminTokenAsync(string username, string password, CancellationToken cancellationToken) =>
        RequestTokenAsync("master", new() { ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = username, ["password"] = password },
            cancellationToken);

    /// <summary>
    /// Sets a confidential client's secret unless it already is that. Null when the realm has no such client, else whether
    /// it changed.
    /// </summary>
    public async Task<bool?> SetClientSecretAsync(string token, string clientId, string secret, CancellationToken cancellationToken)
    {
        var clients = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/clients";
        var (_, found) = await SendAsync<JsonArray>(HttpMethod.Get, $"{clients}?clientId={Uri.EscapeDataString(clientId)}", token, null, cancellationToken);
        if (found?.FirstOrDefault() is not JsonObject client || client["id"]?.GetValue<string>() is not { } id)
        {
            return null;
        }
        var (_, current) = await SendAsync<JsonObject>(HttpMethod.Get, $"{clients}/{id}/client-secret", token, null, cancellationToken);
        if (current?["value"]?.GetValue<string>() == secret)
        {
            return false;
        }
        client["secret"] = secret;
        await SendAsync<JsonNode>(HttpMethod.Put, $"{clients}/{id}", token, client, cancellationToken);
        return true;
    }

    private async Task<string?> FindUserIdAsync(string token, string username, CancellationToken cancellationToken)
    {
        var (_, found) = await SendAsync<JsonArray>(HttpMethod.Get, $"{Users}?username={Uri.EscapeDataString(username)}&exact=true", token, null,
            cancellationToken);
        return found?.FirstOrDefault()?["id"]?.GetValue<string>();
    }

    /// <summary>
    /// One admin API call with a bearer token: fails unless it succeeds or answers one of <paramref name="tolerated"/>; a
    /// successful GET's JSON body comes back.
    /// </summary>
    private async Task<(HttpStatusCode Status, T? Body)> SendAsync<T>(HttpMethod method, string url, string token, JsonNode? body,
        CancellationToken cancellationToken, params HttpStatusCode[] tolerated) where T : JsonNode
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (tolerated.Contains(response.StatusCode))
        {
            return (response.StatusCode, null);
        }
        response.EnsureSuccessStatusCode();
        return (response.StatusCode, method == HttpMethod.Get ? await response.Content.ReadFromJsonAsync<T>(cancellationToken) : null);
    }

    private Task<string> GetAdminTokenAsync(CancellationToken cancellationToken) =>
        RequestTokenAsync(IdentitySettings.Realm, new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = configuration["Identity:AdminClientId"] ?? "tvtracker-identity-admin",
            ["client_secret"] = configuration["Identity:AdminClientSecret"] ?? throw new InvalidOperationException("Missing Identity:AdminClientSecret")
        }, cancellationToken);

    private async Task<string> RequestTokenAsync(string realm, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{BaseUrl}/realms/{realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(form), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))?.AccessToken
            ?? throw new InvalidOperationException("Keycloak returned no access token.");
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}
