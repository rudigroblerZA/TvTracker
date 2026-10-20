using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TvTracker.Persistence;

/// <summary>
/// Creates the schema at startup (and optionally seeds data) before the service starts taking requests.
/// Uses EnsureCreated for now; switch to EF Core migrations before the first production release.
/// Only one process per database should run this (for catalogdb that's Catalog.API).
/// </summary>
public sealed class DatabaseInitializer<TContext>(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializer<TContext>> logger,
    Func<TContext, IServiceProvider, CancellationToken, Task>? seed) : IHostedService
    where TContext : DbContext
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();

                var created = await db.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Database for {Context} ready (schema created: {Created})", typeof(TContext).Name, created);

                if (seed is not null)
                {
                    await seed(db, scope.ServiceProvider, cancellationToken);
                }

                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Database initialisation failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
