namespace Identity.API.Services;

/// <summary>
/// Makes sure the app's admin account (<c>Admin:Account</c>: username and password, the AppHost's <c>app-admin-password</c>)
/// exists in Keycloak with that password. The realm file has it too, but Keycloak imports the realm only on its first
/// start, so a realm made before the account was added gets it here, and an existing account's password is reset to the
/// configured one each start (the realm file's is the development value). Its username is also in <c>Admin:Users</c>,
/// which is what makes it an admin of the app. Retried as <see cref="KeycloakRetry"/> says.
/// </summary>
public sealed class AdminAccountSeeder(KeycloakAdminClient keycloak, IConfiguration config, ILogger<AdminAccountSeeder> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["Admin:Account:Username"] is not { Length: > 0 } username || config["Admin:Account:Password"] is not { Length: > 0 } password)
        {
            return;
        }

        await KeycloakRetry.RunAsync(logger, "set up the admin account", async () =>
        {
            if (await keycloak.EnsureUserAsync(username, password, $"{username}@example.com", "App", "Admin", stoppingToken))
            {
                logger.LogInformation("Created the admin account {Username} in Keycloak", username);
            }
        }, stoppingToken);
    }
}
