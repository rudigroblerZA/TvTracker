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
    /// Whether Keycloak has a user with this id.
    /// </summary>
    public async Task<bool> UserExistsAsync(string userId, CancellationToken cancellationToken)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        var (status, _) = await SendAsync<JsonObject>(HttpMethod.Get, $"{Users}/{Uri.EscapeDataString(userId)}", token, null, cancellationToken,
            HttpStatusCode.NotFound);
        return status != HttpStatusCode.NotFound;
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
            await ResetPasswordAsync(token, id, password, cancellationToken);
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
        if (status != HttpStatusCode.Conflict)
        {
            return true;
        }
        // Created between the lookup and the create (the realm import finishing): still give it this password.
        if (await FindUserIdAsync(token, username, cancellationToken) is { } created)
        {
            await ResetPasswordAsync(token, created, password, cancellationToken);
        }
        return false;
    }

    private Task ResetPasswordAsync(string token, string userId, string password, CancellationToken cancellationToken) =>
        SendAsync<JsonNode>(HttpMethod.Put, $"{Users}/{userId}/reset-password", token,
            new JsonObject { ["type"] = "password", ["value"] = password, ["temporary"] = false }, cancellationToken);

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
        if (await FindClientAsync(token, clientId, cancellationToken) is not var (client, id))
        {
            return null;
        }
        var (_, current) = await SendAsync<JsonObject>(HttpMethod.Get, $"{Clients}/{id}/client-secret", token, null, cancellationToken);
        if (current?["value"]?.GetValue<string>() == secret)
        {
            return false;
        }
        client["secret"] = secret;
        await SendAsync<JsonNode>(HttpMethod.Put, $"{Clients}/{id}", token, client, cancellationToken);
        return true;
    }

    /// <summary>
    /// Creates a service client (<see cref="TvTracker.ServiceClients.Definition"/>) unless the realm has it. True when it
    /// created it.
    /// </summary>
    public async Task<bool> EnsureServiceClientAsync(string token, string clientId, string name, string secret, CancellationToken cancellationToken)
    {
        if (await FindClientAsync(token, clientId, cancellationToken) is not null)
        {
            return false;
        }
        var (status, _) = await SendAsync<JsonNode>(HttpMethod.Post, Clients, token,
            System.Text.Json.JsonSerializer.SerializeToNode(TvTracker.ServiceClients.Definition(clientId, name, secret)), cancellationToken,
            HttpStatusCode.Conflict);
        return status != HttpStatusCode.Conflict;
    }

    /// <summary>
    /// Makes the realm require HTTPS from outside addresses and lock an account for a while after repeated wrong
    /// passwords; false when it already does. The realm file (made for development) has neither.
    /// </summary>
    public async Task<bool> HardenRealmAsync(string token, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}";
        var (_, realm) = await SendAsync<JsonObject>(HttpMethod.Get, url, token, null, cancellationToken);
        if (realm is null || (realm["sslRequired"]?.GetValue<string>() == "external" && realm["bruteForceProtected"]?.GetValue<bool>() == true))
        {
            return false;
        }
        await SendAsync<JsonNode>(HttpMethod.Put, url, token, new JsonObject { ["sslRequired"] = "external", ["bruteForceProtected"] = true },
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Gives the realm a mail account, so Keycloak can send what it has to: the link to reset a forgotten password,
    /// and the one that confirms a new account's address. Returns whether anything changed.
    /// <list type="bullet">
    /// <item><b>Confirming addresses</b> (<paramref name="verifyEmail"/>) is switched on with it, since it's the
    /// mail account that makes it possible: a new account then can't be used until its address is confirmed, and
    /// the app's own emails go only to addresses that are someone's.</item>
    /// <item><b>The password is always sent:</b> Keycloak never gives a stored one back, so there's nothing to
    /// compare it with; "changed" is judged on everything else.</item>
    /// </list>
    /// </summary>
    public async Task<bool> SetMailAccountAsync(string token, MailAccount mail, bool verifyEmail, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}";
        var (_, realm) = await SendAsync<JsonObject>(HttpMethod.Get, url, token, null, cancellationToken);
        var wanted = new JsonObject
        {
            ["host"] = mail.Host,
            ["port"] = mail.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["from"] = mail.From,
            ["fromDisplayName"] = mail.FromName,
            ["auth"] = mail.User is null ? "false" : "true",
            ["user"] = mail.User ?? "",
            ["starttls"] = mail.Encryption == MailEncryption.StartTls ? "true" : "false",
            ["ssl"] = mail.Encryption == MailEncryption.Ssl ? "true" : "false"
        };
        var current = realm?["smtpServer"] as JsonObject;
        var same = current is not null && wanted.All(w => current[w.Key]?.GetValue<string>() == w.Value!.GetValue<string>())
            && realm?["verifyEmail"]?.GetValue<bool>() == verifyEmail && realm["resetPasswordAllowed"]?.GetValue<bool>() == true;
        if (mail.Password is not null)
        {
            wanted["password"] = mail.Password;
        }
        await SendAsync<JsonNode>(HttpMethod.Put, url, token,
            new JsonObject { ["smtpServer"] = wanted, ["verifyEmail"] = verifyEmail, ["resetPasswordAllowed"] = true }, cancellationToken);
        return !same;
    }

    /// <summary>
    /// Lets people sign up and sign in with an outside account (<paramref name="alias"/> is Keycloak's name for the
    /// provider, such as "google"): creates the realm's identity provider with the app's client id and secret there,
    /// or brings an existing one up to date. Returns what it did, for the log.
    /// <list type="bullet">
    /// <item><b>The address is trusted:</b> the provider has confirmed it, so the account starts with a verified
    /// email.</item>
    /// <item><b>Always written when it exists:</b> Keycloak never gives a stored secret back, so there's nothing to
    /// compare a new one with.</item>
    /// </list>
    /// </summary>
    public async Task<string> EnsureIdentityProviderAsync(string token, string alias, string clientId, string clientSecret,
        CancellationToken cancellationToken)
    {
        var providers = $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/identity-provider/instances";
        var provider = new JsonObject
        {
            ["alias"] = alias,
            ["providerId"] = alias,
            ["enabled"] = true,
            ["trustEmail"] = true,
            ["config"] = new JsonObject
            {
                ["clientId"] = clientId,
                ["clientSecret"] = clientSecret,
                ["defaultScope"] = "openid profile email",
                // Name and address are taken when the account is made, and are the person's to change after.
                ["syncMode"] = "IMPORT"
            }
        };
        var (status, existing) = await SendAsync<JsonObject>(HttpMethod.Get, $"{providers}/{alias}", token, null, cancellationToken,
            HttpStatusCode.NotFound);
        if (status == HttpStatusCode.NotFound)
        {
            await SendAsync<JsonNode>(HttpMethod.Post, providers, token, provider, cancellationToken);
            return "added";
        }
        // Keeps what an admin set by hand in Keycloak's console (mappers aside, which live elsewhere).
        if (existing?["internalId"]?.GetValue<string>() is { } internalId)
        {
            provider["internalId"] = internalId;
        }
        await SendAsync<JsonNode>(HttpMethod.Put, $"{providers}/{alias}", token, provider, cancellationToken);
        return existing?["config"]?["clientId"]?.GetValue<string>() == clientId && existing["enabled"]?.GetValue<bool>() == true
            ? "kept"
            : "updated";
    }

    /// <summary>
    /// Adds <paramref name="pattern"/> to a client's allowed redirect and post-logout redirect addresses unless it's
    /// there. Null when the realm has no such client, else whether it changed.
    /// </summary>
    public async Task<bool?> AllowRedirectAsync(string token, string clientId, string pattern, CancellationToken cancellationToken)
    {
        if (await FindClientAsync(token, clientId, cancellationToken) is not var (client, id))
        {
            return null;
        }
        var redirects = client["redirectUris"] as JsonArray ?? [];
        var attributes = client["attributes"] as JsonObject ?? [];
        // Keycloak keeps post-logout addresses as one attribute, separated by "##".
        var logouts = (attributes["post.logout.redirect.uris"]?.GetValue<string>() ?? "").Split("##", StringSplitOptions.RemoveEmptyEntries).ToList();
        var known = redirects.Any(r => r?.GetValue<string>() == pattern);
        if (known && logouts.Contains(pattern))
        {
            return false;
        }
        if (!known)
        {
            redirects.Add(pattern);
        }
        if (!logouts.Contains(pattern))
        {
            logouts.Add(pattern);
        }
        client["redirectUris"] = redirects.DeepClone();
        attributes["post.logout.redirect.uris"] = string.Join("##", logouts);
        client["attributes"] = attributes.DeepClone();
        await SendAsync<JsonNode>(HttpMethod.Put, $"{Clients}/{id}", token, client, cancellationToken);
        return true;
    }

    private string Clients => $"{BaseUrl}/admin/realms/{IdentitySettings.Realm}/clients";

    private async Task<(JsonObject Client, string Id)?> FindClientAsync(string token, string clientId, CancellationToken cancellationToken)
    {
        var (_, found) = await SendAsync<JsonArray>(HttpMethod.Get, $"{Clients}?clientId={Uri.EscapeDataString(clientId)}", token, null, cancellationToken);
        return found?.FirstOrDefault() is JsonObject client && client["id"]?.GetValue<string>() is { } id ? (client, id) : null;
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

/// <summary>
/// How a connection to the mail server is protected.
/// </summary>
public enum MailEncryption
{
    /// <summary>
    /// Starts plain and upgrades (STARTTLS): what port 587 expects.
    /// </summary>
    StartTls,

    /// <summary>
    /// Encrypted from the first byte: what port 465 expects.
    /// </summary>
    Ssl,

    /// <summary>
    /// None: a test server on the same machine only.
    /// </summary>
    None
}

/// <summary>
/// The mail (SMTP) account Keycloak sends through (<c>Keycloak:Smtp</c>).
/// </summary>
/// <param name="From">The address its mail comes from.</param>
/// <param name="FromName">The name shown beside it: the app's.</param>
/// <param name="User">The account's user name; null for a server that asks for none.</param>
public sealed record MailAccount(string Host, int Port, string From, string FromName, string? User, string? Password, MailEncryption Encryption)
{
    /// <summary>
    /// The account in configuration, or null when there's none (no host or no from address): nothing is then set
    /// in the realm, and one put there by hand stays.
    /// <list type="bullet">
    /// <item><b>Port:</b> 587 when not given.</item>
    /// <item><b>Encryption:</b> <c>Keycloak:Smtp:Encryption</c> (starttls, ssl or none) when given; else SSL on
    /// port 465, none on 25 and 1025 (a local test server), and STARTTLS on anything else.</item>
    /// </list>
    /// </summary>
    public static MailAccount? FromConfiguration(IConfiguration configuration, string appName)
    {
        var section = configuration.GetSection("Keycloak:Smtp");
        if (Text(section["Host"]) is not { } host || Text(section["From"]) is not { } from)
        {
            return null;
        }
        var port = int.TryParse(section["Port"], out var given) && given is > 0 and < 65536 ? given : 587;
        var encryption = Text(section["Encryption"])?.ToLowerInvariant() switch
        {
            "ssl" or "tls" => MailEncryption.Ssl,
            "starttls" => MailEncryption.StartTls,
            "none" => MailEncryption.None,
            _ => port switch { 465 => MailEncryption.Ssl, 25 or 1025 => MailEncryption.None, _ => MailEncryption.StartTls }
        };
        return new MailAccount(host, port, from, Text(section["FromName"]) ?? appName, Text(section["User"]), Text(section["Password"]), encryption);
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
