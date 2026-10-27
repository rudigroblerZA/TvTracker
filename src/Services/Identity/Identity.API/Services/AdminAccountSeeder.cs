namespace Identity.API.Services;

/// <summary>
/// Makes sure the app's admin account (<c>Admin:Account</c>: username and password, set by the AppHost in development)
/// exists in Keycloak. The realm file has it too, but Keycloak imports the realm only on its first start, so a realm
/// made before the account was added gets it here. Its username is also in <c>Admin:Users</c>, which is what makes it
/// an admin of the app.
/// </summary>
public sealed class AdminAccountSeeder(KeycloakAdminClient keycloak, IConfiguration config, ILogger<AdminAccountSeeder> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["Admin:Account:Username"] is not { Length: > 0 } username || config["Admin:Account:Password"] is not { Length: > 0 } password)
        {
            return;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (await keycloak.EnsureUserAsync(username, password, $"{username}@example.com", "App", "Admin", stoppingToken))
                {
                    logger.LogInformation("Created the admin account {Username} in Keycloak", username);
                }
                return;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Couldn't check the admin account in Keycloak (attempt {Attempt}); trying again", attempt);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}
