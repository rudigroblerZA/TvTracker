using Identity.API.Apis;
using Identity.API.Data;
using Identity.API.Payments;
using TvTracker.Persistence;

namespace Identity.API.Services;

/// <summary>
/// Outside Development, deletes the realm file's demo account (demo / demo) on every start, through the ordinary account
/// deletion, so anything made with it in the meantime goes from every service too. Keycloak imports the realm file,
/// demo user and all, the first time it starts on an empty database, so a new production realm would otherwise open with
/// a known login. Development keeps it: it's how the app is tried.
/// <list type="bullet">
/// <item><b>By id, not name:</b> the realm file gives the demo user a fixed id (<see cref="UserId"/>), so a person who
/// later registers the username "demo" is never touched.</item>
/// <item>Retried as <see cref="KeycloakRetry"/> says.</item>
/// </list>
/// </summary>
public sealed class DemoAccountRemover(IServiceScopeFactory scopes, ILogger<DemoAccountRemover> logger) : BackgroundService
{
    /// <summary>
    /// The demo user's id in <c>Realms/tvtracker-realm.json</c>.
    /// </summary>
    public const string UserId = "0d3e0000-de40-4000-8000-000000000001";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        KeycloakRetry.RunAsync(logger, "remove the demo account", async () =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var keycloak = services.GetRequiredService<KeycloakAdminClient>();
            if (!await keycloak.UserExistsAsync(UserId, stoppingToken))
            {
                return;
            }
            await ProfileApi.DeleteAccountByIdAsync(UserId, services.GetRequiredService<IdentityContext>(), services.GetRequiredService<IIntegrationEventOutbox>(),
                keycloak, services.GetRequiredService<TimeProvider>(), services.GetRequiredService<PaymentProviders>(), logger, stoppingToken);
            logger.LogWarning("Deleted the demo account: it comes with the realm file and isn't for production");
        }, stoppingToken);
}
