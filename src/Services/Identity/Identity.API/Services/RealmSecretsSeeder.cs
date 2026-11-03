namespace Identity.API.Services;

/// <summary>
/// Puts the AppHost's client secrets (<c>Keycloak:ClientSecrets:{clientId}</c>) into Keycloak's realm on every start. The
/// app admin's password is <see cref="AdminAccountSeeder"/>'s, which resets it each start.
/// <list type="bullet">
/// <item><b>Why:</b> the realm file carries the development values and Keycloak imports it only on its first start, so a
/// deployment's realm would otherwise keep secrets anyone can read in the repository. With this, the AppHost's secret
/// parameters are the one place they're set.</item>
/// <item><b>Through the master realm's admin</b> (<c>Keycloak:AdminUsername</c> / <c>AdminPassword</c>), as the MCP server
/// creates its client: Identity's own service account can manage users but not clients, and its secret is one of those
/// being set.</item>
/// <item><b>Idempotent:</b> a client whose secret already matches is left alone, so in development (values equal to the
/// realm file's) nothing changes.</item>
/// <item><b>Retried</b> every 30 s on any failure, 20 times (Keycloak still starting, a timeout, an error page); after that
/// an error is logged, since every service using a changed secret gets 401 from Keycloak until Identity restarts.</item>
/// </list>
/// </summary>
public sealed class RealmSecretsSeeder(KeycloakAdminClient keycloak, IConfiguration config, ILogger<RealmSecretsSeeder> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 20;

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
                var token = await keycloak.GetMasterAdminTokenAsync(adminUser, adminPassword, stoppingToken);
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
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(ex, "Gave up setting the realm's secrets in Keycloak after {Attempts} attempts; services whose "
                        + "secret changed get 401 from Keycloak until Identity.API restarts", attempt);
                    return;
                }
                logger.LogWarning(ex, "Couldn't set the realm's secrets in Keycloak (attempt {Attempt}); trying again", attempt);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}
