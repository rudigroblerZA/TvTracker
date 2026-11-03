using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Identity.API.Services;

/// <summary>
/// Puts the AppHost's secrets into Keycloak's realm on every start: each confidential client's secret
/// (<c>Keycloak:ClientSecrets:{clientId}</c>) and the app admin's password (<c>Admin:Account</c>).
/// <list type="bullet">
/// <item><b>Why:</b> the realm file carries the development values and Keycloak imports it only on its first start, so a
/// deployment's realm would otherwise keep secrets anyone can read in the repository. With this, the AppHost's secret
/// parameters are the one place they're set.</item>
/// <item><b>Through the master realm's admin</b> (<c>Keycloak:AdminUsername</c> / <c>AdminPassword</c>), as the MCP server
/// creates its client: Identity's own service account can manage users but not clients, and its secret is one of those
/// being set.</item>
/// <item><b>Idempotent:</b> a client whose secret already matches is left alone, so in development (values equal to the
/// realm file's) nothing changes. The admin's password is reset each start, so the AppHost's value is the one that holds.</item>
/// <item>Retried every 30 s until Keycloak answers; until then, services using a changed secret get 401 from Keycloak.</item>
/// </list>
/// </summary>
public sealed class RealmSecretsSeeder(HttpClient http, IConfiguration config, ILogger<RealmSecretsSeeder> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 20;

    private string BaseUrl => (config["Identity:Url"] ?? throw new InvalidOperationException("Missing Identity:Url")).TrimEnd('/');

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["Keycloak:AdminUsername"] is not { Length: > 0 } adminUser || config["Keycloak:AdminPassword"] is not { Length: > 0 } adminPassword)
        {
            return;
        }
        var secrets = config.GetSection("Keycloak:ClientSecrets").GetChildren()
            .Where(c => c.Value is { Length: > 0 })
            .ToDictionary(c => c.Key, c => c.Value!);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var token = await SignInAsync(adminUser, adminPassword, stoppingToken);
                foreach (var (clientId, secret) in secrets)
                {
                    await SetClientSecretAsync(token, clientId, secret, stoppingToken);
                }
                if (config["Admin:Account:Username"] is { Length: > 0 } username && config["Admin:Account:Password"] is { Length: > 0 } password)
                {
                    await SetPasswordAsync(token, username, password, stoppingToken);
                }
                return;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Couldn't set the realm's secrets in Keycloak (attempt {Attempt}); trying again", attempt);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }

    private async Task<string> SignInAsync(string username, string password, CancellationToken ct)
    {
        using var response = await http.PostAsync($"{BaseUrl}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = username, ["password"] = password }), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(ct))?.AccessToken
            ?? throw new HttpRequestException("Keycloak returned no admin token.");
    }

    /// <summary>
    /// Sets a client's secret to <paramref name="secret"/> unless it already is; a client the realm lacks is skipped (the
    /// MCP server creates its own).
    /// </summary>
    private async Task SetClientSecretAsync(string token, string clientId, string secret, CancellationToken ct)
    {
        var clients = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/clients";
        var found = await SendAsync<JsonArray>(HttpMethod.Get, $"{clients}?clientId={Uri.EscapeDataString(clientId)}", token, null, ct);
        if (found?.FirstOrDefault() is not JsonObject client || client["id"]?.GetValue<string>() is not { } id)
        {
            logger.LogInformation("Keycloak has no client {ClientId} yet; its secret is set when it's created", clientId);
            return;
        }

        var current = await SendAsync<JsonObject>(HttpMethod.Get, $"{clients}/{id}/client-secret", token, null, ct);
        if (current?["value"]?.GetValue<string>() == secret)
        {
            return;
        }
        client["secret"] = secret;
        await SendAsync<JsonObject>(HttpMethod.Put, $"{clients}/{id}", token, client, ct);
        logger.LogInformation("Set the secret of the Keycloak client {ClientId} from the AppHost", clientId);
    }

    private async Task SetPasswordAsync(string token, string username, string password, CancellationToken ct)
    {
        var users = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/users";
        var found = await SendAsync<JsonArray>(HttpMethod.Get, $"{users}?username={Uri.EscapeDataString(username)}&exact=true", token, null, ct);
        if (found?.FirstOrDefault()?["id"]?.GetValue<string>() is not { } id)
        {
            return; // AdminAccountSeeder creates it, with this password
        }
        await SendAsync<JsonObject>(HttpMethod.Put, $"{users}/{id}/reset-password", token,
            new JsonObject { ["type"] = "password", ["value"] = password, ["temporary"] = false }, ct);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, string token, JsonNode? body, CancellationToken ct) where T : JsonNode
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentLength == 0 || method != HttpMethod.Get
            ? null
            : await response.Content.ReadFromJsonAsync<T>(ct);
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}
