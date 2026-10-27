using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TvTracker.LoadGen;

/// <summary>Raised for anything that should stop the tool with a message rather than a stack trace.</summary>
public sealed class CliException(string message) : Exception(message);

/// <summary>
/// Keycloak for load testing: the admin API (as the master realm's admin) to create the load-test client and users,
/// and the password grant to sign them in. The realm's own clients don't allow password sign-in, so
/// <see cref="ClientId"/> exists only for this tool, in development.
/// </summary>
public sealed class Keycloak(HttpClient http, string baseUrl)
{
    public const string Realm = "tvtracker";
    public const string ClientId = "tvtracker-loadgen";

    private string Base => baseUrl.TrimEnd('/');

    /// <summary>Signs in a user; returns the access token and when to refresh it (at 80% of its life).</summary>
    public async Task<(string Token, DateTimeOffset RefreshAt)> SignInAsync(string username, string password, CancellationToken ct)
    {
        using var response = await http.PostAsync($"{Base}/realms/{Realm}/protocol/openid-connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = ClientId, ["username"] = username, ["password"] = password }), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException($"Couldn't sign in {username} ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}. " +
                "Run `tvtracker-load setup` first.");
        }
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(ct))!;
        return (token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn * 0.8));
    }

    /// <summary>A master-realm admin token for the admin API.</summary>
    public async Task<string> AdminTokenAsync(string username, string password, CancellationToken ct)
    {
        using var response = await http.PostAsync($"{Base}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = username, ["password"] = password }), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException($"Keycloak refused the admin sign-in ({(int)response.StatusCode}). Is the AppHost running, and is --admin-password right?");
        }
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(ct))!.AccessToken;
    }

    /// <summary>Creates the load-test client unless it exists: public, password grant on, tokens for the services' audience.</summary>
    public async Task<bool> EnsureClientAsync(string adminToken, CancellationToken ct)
    {
        var existing = await AdminGetAsync<List<object>>(adminToken, $"clients?clientId={ClientId}", ct);
        if (existing.Count > 0)
        {
            return false;
        }
        await AdminPostAsync(adminToken, "clients", new
        {
            clientId = ClientId,
            name = "Load test (tvtracker-load)",
            enabled = true,
            publicClient = true,
            standardFlowEnabled = false,
            directAccessGrantsEnabled = true,
            protocolMappers = new[]
            {
                new
                {
                    name = "tvtracker-api-audience",
                    protocol = "openid-connect",
                    protocolMapper = "oidc-audience-mapper",
                    config = new Dictionary<string, string>
                    {
                        ["included.custom.audience"] = "tvtracker-api",
                        ["id.token.claim"] = "false",
                        ["access.token.claim"] = "true"
                    }
                }
            }
        }, ct);
        return true;
    }

    /// <summary>Creates a user with a password unless one with that username exists. True when it created one.</summary>
    public async Task<bool> EnsureUserAsync(string adminToken, string username, string displayNumber, string password, CancellationToken ct)
    {
        var existing = await AdminGetAsync<List<object>>(adminToken, $"users?username={Uri.EscapeDataString(username)}&exact=true", ct);
        if (existing.Count > 0)
        {
            return false;
        }
        // Keycloak 26 wants a full profile (email, first and last name), or sign-in asks for one.
        await AdminPostAsync(adminToken, "users", new
        {
            username,
            email = $"{username}@example.com",
            firstName = "Load",
            lastName = $"Tester {displayNumber}",
            enabled = true,
            emailVerified = true,
            credentials = new[] { new { type = "password", value = password, temporary = false } }
        }, ct);
        return true;
    }

    public async Task<List<string>> FindUsersAsync(string adminToken, string prefix, CancellationToken ct)
    {
        var users = await AdminGetAsync<List<UserRepresentation>>(adminToken, $"users?search={Uri.EscapeDataString(prefix)}&max=10000", ct);
        return users.Select(u => u.Username).Where(u => u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Order().ToList();
    }

    private async Task<T> AdminGetAsync<T>(string token, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/admin/realms/{Realm}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private async Task AdminPostAsync(string token, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/admin/realms/{Realm}/{path}") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            throw new CliException($"Keycloak refused {path} ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record UserRepresentation(string Username);
}
