using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Identity.API.Services;

/// <summary>
/// Calls the Keycloak admin API with the "tvtracker-identity-admin" service account
/// (client credentials; it has the realm-management manage-users role), or, for setting the realm's secrets
/// (<see cref="RealmSecretsSeeder"/>), with a master-realm admin token from <see cref="GetMasterAdminTokenAsync"/>.
/// </summary>
public sealed class KeycloakAdminClient(HttpClient httpClient, IConfiguration configuration, ILogger<KeycloakAdminClient> logger)
{
    private string BaseUrl => (configuration["Identity:Url"] ?? throw new InvalidOperationException("Missing Identity:Url")).TrimEnd('/');

    public async Task DeleteUserAsync(string userId, CancellationToken cancellationToken)
    {
        var token = await GetAdminTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/users/{Uri.EscapeDataString(userId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogInformation("Keycloak user {UserId} was already deleted", userId);
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates a user with a password unless one with that username exists. Returns true when it created one.
    /// </summary>
    public async Task<bool> EnsureUserAsync(string username, string password, string email, string firstName, string lastName,
        CancellationToken cancellationToken)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        var users = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/users";

        using var find = new HttpRequestMessage(HttpMethod.Get, $"{users}?username={Uri.EscapeDataString(username)}&exact=true");
        find.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var found = await httpClient.SendAsync(find, cancellationToken);
        found.EnsureSuccessStatusCode();
        if ((await found.Content.ReadFromJsonAsync<List<object>>(cancellationToken))?.Count > 0)
        {
            return false;
        }

        using var create = new HttpRequestMessage(HttpMethod.Post, users)
        {
            Content = JsonContent.Create(new
            {
                username,
                email,
                firstName,
                lastName,
                enabled = true,
                emailVerified = true,
                credentials = new[] { new { type = "password", value = password, temporary = false } }
            })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var created = await httpClient.SendAsync(create, cancellationToken);
        if (created.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
        created.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// A token for the master realm's admin (password grant through admin-cli), which can manage clients.
    /// </summary>
    public async Task<string> GetMasterAdminTokenAsync(string username, string password, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{BaseUrl}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = username, ["password"] = password }),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))?.AccessToken
            ?? throw new InvalidOperationException("Keycloak returned no admin token.");
    }

    /// <summary>
    /// Sets a confidential client's secret unless it already is that. Null when the realm has no such client, else whether
    /// it changed.
    /// </summary>
    public async Task<bool?> SetClientSecretAsync(string token, string clientId, string secret, CancellationToken cancellationToken)
    {
        var clients = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/clients";
        var found = await SendAsync<JsonArray>(HttpMethod.Get, $"{clients}?clientId={Uri.EscapeDataString(clientId)}", token, null, cancellationToken);
        if (found?.FirstOrDefault() is not JsonObject client || client["id"]?.GetValue<string>() is not { } id)
        {
            return null;
        }
        var current = await SendAsync<JsonObject>(HttpMethod.Get, $"{clients}/{id}/client-secret", token, null, cancellationToken);
        if (current?["value"]?.GetValue<string>() == secret)
        {
            return false;
        }
        client["secret"] = secret;
        await SendAsync<JsonObject>(HttpMethod.Put, $"{clients}/{id}", token, client, cancellationToken);
        return true;
    }

    /// <summary>
    /// Resets a realm user's password. False when there's no such user.
    /// </summary>
    public async Task<bool> SetPasswordAsync(string token, string username, string password, CancellationToken cancellationToken)
    {
        var users = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/users";
        var found = await SendAsync<JsonArray>(HttpMethod.Get, $"{users}?username={Uri.EscapeDataString(username)}&exact=true", token, null, cancellationToken);
        if (found?.FirstOrDefault()?["id"]?.GetValue<string>() is not { } id)
        {
            return false;
        }
        await SendAsync<JsonObject>(HttpMethod.Put, $"{users}/{id}/reset-password", token,
            new JsonObject { ["type"] = "password", ["value"] = password, ["temporary"] = false }, cancellationToken);
        return true;
    }

    /// <summary>
    /// One admin API call with a bearer token; a GET's JSON body is returned, other methods return null.
    /// </summary>
    private async Task<T?> SendAsync<T>(HttpMethod method, string url, string token, JsonNode? body, CancellationToken cancellationToken)
        where T : JsonNode
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return method == HttpMethod.Get ? await response.Content.ReadFromJsonAsync<T>(cancellationToken) : null;
    }

    private async Task<string> GetAdminTokenAsync(CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = configuration["Identity:AdminClientId"] ?? "tvtracker-identity-admin",
            ["client_secret"] = configuration["Identity:AdminClientSecret"]
                ?? throw new InvalidOperationException("Missing Identity:AdminClientSecret")
        });

        using var response = await httpClient.PostAsync(
            $"{BaseUrl}/realms/{IdentitySettings.Realm}/protocol/openid-connect/token", form, cancellationToken);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return token?.AccessToken ?? throw new InvalidOperationException("Keycloak returned no access token.");
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}
