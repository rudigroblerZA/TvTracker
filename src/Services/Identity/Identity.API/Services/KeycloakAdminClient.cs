using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace Identity.API.Services;

/// <summary>
/// Calls the Keycloak admin API with the "tvtracker-identity-admin" service account
/// (client credentials; it has the realm-management manage-users role).
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

    /// <summary>Creates a user with a password unless one with that username exists. Returns true when it created one.</summary>
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
