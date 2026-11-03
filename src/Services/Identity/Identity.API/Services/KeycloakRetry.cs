namespace Identity.API.Services;

/// <summary>
/// The start-up seeders' retry: every 30 s, 20 times, on any failure (Keycloak still starting, a timeout, an error
/// page, a refused value), then an error log. Shared so the seeders handle failures the same way; a seeder that threw
/// out of <c>ExecuteAsync</c> would stop Identity.API.
/// </summary>
public static class KeycloakRetry
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 20;

    public static async Task RunAsync(ILogger logger, string what, Func<Task> work, CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await work();
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(ex, "Gave up trying to {What} in Keycloak after {Attempts} attempts", what, attempt);
                    return;
                }
                logger.LogWarning(ex, "Couldn't {What} in Keycloak (attempt {Attempt}); trying again", what, attempt);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}
