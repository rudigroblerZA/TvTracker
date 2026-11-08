namespace Identity.API.Services;

/// <summary>
/// Outside Development, deletes the realm file's <c>demo</c> account (password <c>demo</c>) from Keycloak on every start.
/// Keycloak imports the realm file, demo user and all, the first time it starts on an empty database, so a new
/// production realm would otherwise open with a known login. Development keeps it: it's how the app is tried.
/// Retried as <see cref="KeycloakRetry"/> says.
/// </summary>
public sealed class DemoAccountRemover(KeycloakAdminClient keycloak, ILogger<DemoAccountRemover> logger) : BackgroundService
{
    public const string Username = "demo";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        KeycloakRetry.RunAsync(logger, "remove the demo account", async () =>
        {
            if (await keycloak.DeleteUserByNameAsync(Username, stoppingToken))
            {
                logger.LogWarning("Deleted the demo account from Keycloak: it comes with the realm file and isn't for production");
            }
        }, stoppingToken);
}
