namespace Identity.API.Services;

/// <summary>
/// Puts the AppHost's client secrets (<c>Keycloak:ClientSecrets:{clientId}</c>) into Keycloak's realm on every start. The
/// app admin's password is <see cref="AdminAccountSeeder"/>'s, which resets it each start.
/// <list type="bullet">
/// <item><b>Why:</b> the realm file carries the development values and Keycloak imports it only on its first start, so a
/// deployment's realm would otherwise keep secrets anyone can read in the repository. With this, the AppHost's secret
/// parameters are the one place they're set.</item>
/// <item><b>Through the master realm's admin</b> (<c>Keycloak:AdminUsername</c> / <c>AdminPassword</c>): Identity's own service account can manage users but not clients, and its secret is one of those
/// being set.</item>
/// <item><b>Service clients</b> (<c>Keycloak:ServiceClients:{clientId}</c> = its display name, such as
/// <c>tvtracker-internal</c>, which other services use to call the sync worker) are created when the realm lacks them,
/// with their secret from <c>Keycloak:ClientSecrets</c>: a realm imported before the client was added to the realm file
/// would otherwise refuse every internal call, and only this service holds the master realm's admin.</item>
/// <item><b>Idempotent:</b> a client whose secret already matches is left alone, so in development (values equal to the
/// realm file's) nothing changes.</item>
/// <item><b>The web app's public address</b> (<c>Keycloak:WebPublicUrl</c>, set when the web app is reached through
/// one forwarded address, such as a cloud preview): added to the web client's allowed redirects, which the realm file has
/// only for localhost.</item>
/// <item><b>Sign in with Google</b> (<c>Keycloak:Google:ClientId</c> and <c>:ClientSecret</c>): the realm's Google
/// identity provider is created or brought up to date, so the sign-in page offers it. Without them nothing is touched,
/// and one already in the realm stays as it is.</item>
/// <item><b>Outside development</b> the realm is also set to require HTTPS from outside addresses and to lock an
/// account for a while after repeated wrong passwords, which the realm file leaves off.</item>
/// <item><b>Retried</b> as <see cref="KeycloakRetry"/> says; when it gives up, every service using a changed secret gets
/// 401 from Keycloak until Identity restarts.</item>
/// </list>
/// </summary>
public sealed class RealmSecretsSeeder(KeycloakAdminClient keycloak, IConfiguration config, IHostEnvironment environment,
    ILogger<RealmSecretsSeeder> logger) : BackgroundService
{
    private const string WebClientId = "tvtracker-web";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["Keycloak:AdminUsername"] is not { Length: > 0 } adminUser || config["Keycloak:AdminPassword"] is not { Length: > 0 } adminPassword)
        {
            return;
        }
        var secrets = config.GetSection("Keycloak:ClientSecrets").GetChildren()
            .Where(c => c.Value is { Length: > 0 })
            .ToDictionary(c => c.Key, c => c.Value!);

        var serviceClients = config.GetSection("Keycloak:ServiceClients").GetChildren()
            .Where(c => c.Value is { Length: > 0 } && secrets.ContainsKey(c.Key))
            .ToDictionary(c => c.Key, c => c.Value!);

        var webPublicUrl = config["Keycloak:WebPublicUrl"] is { Length: > 0 } url ? $"{url.TrimEnd('/')}/*" : null;

        await KeycloakRetry.RunAsync(logger, "set the realm's client secrets", async () =>
        {
            var token = await keycloak.GetMasterAdminTokenAsync(adminUser, adminPassword, stoppingToken);
            foreach (var (clientId, name) in serviceClients)
            {
                if (await keycloak.EnsureServiceClientAsync(token, clientId, name, secrets[clientId], stoppingToken))
                {
                    logger.LogInformation("Created the Keycloak service client {ClientId}", clientId);
                }
            }
            foreach (var (clientId, secret) in secrets)
            {
                switch (await keycloak.SetClientSecretAsync(token, clientId, secret, stoppingToken))
                {
                    case null:
                        logger.LogInformation("Keycloak has no client {ClientId} yet; its secret is set when it's created", clientId);
                        break;
                    case true:
                        logger.LogInformation("Set the secret of the Keycloak client {ClientId} from the AppHost", clientId);
                        break;
                }
            }
            if (!environment.IsDevelopment() && await keycloak.HardenRealmAsync(token, stoppingToken))
            {
                logger.LogInformation("Set the Keycloak realm to require HTTPS and to lock accounts after repeated wrong passwords");
            }
            // Sign in with Google, when the app has been given its Google client (Keycloak:Google:ClientId and
            // :ClientSecret, from the AppHost's google-client-id and google-client-secret).
            if (config["Keycloak:Google:ClientId"] is { Length: > 0 } googleId && config["Keycloak:Google:ClientSecret"] is { Length: > 0 } googleSecret)
            {
                var outcome = await keycloak.EnsureIdentityProviderAsync(token, "google", googleId, googleSecret, stoppingToken);
                if (outcome != "kept")
                {
                    logger.LogInformation("Sign in with Google: {Outcome} the Keycloak identity provider", outcome);
                }
            }
            if (webPublicUrl is not null && await keycloak.AllowRedirectAsync(token, WebClientId, webPublicUrl, stoppingToken) == true)
            {
                logger.LogInformation("Allowed {Address} as a redirect of the Keycloak client {ClientId}", webPublicUrl, WebClientId);
            }
        }, stoppingToken);
    }
}
