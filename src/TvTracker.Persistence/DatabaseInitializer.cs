using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TvTracker.Persistence;

/// <summary>
/// Creates or upgrades the schema at startup (and optionally seeds data) before the service starts taking requests.
/// Contexts with EF Core migrations are migrated; the others still use EnsureCreated, which never changes an
/// existing schema, until they move to migrations before the first production release.
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

                if (db.Database.GetMigrations().Any())
                {
                    await MigrateAsync(db, cancellationToken);
                }
                else
                {
                    var created = await db.Database.EnsureCreatedAsync(cancellationToken);
                    logger.LogInformation("Database for {Context} ready (schema created: {Created})", typeof(TContext).Name, created);
                }

                if (seed is not null)
                {
                    await seed(db, scope.ServiceProvider, cancellationToken);
                }

                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is not (OperationCanceledException or PreMigrationsDatabaseException))
            {
                logger.LogWarning(ex, "Database initialisation failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task MigrateAsync(TContext db, CancellationToken cancellationToken)
    {
        // Checked first: asking a new database for its applied migrations logs failed queries for the missing history table.
        var (tables, hasHistory) = await GetSchemaStateAsync(db, cancellationToken);
        if (!hasHistory && tables > 0)
        {
            // Created by EnsureCreated before this context had migrations: there's no history to migrate from.
            throw new PreMigrationsDatabaseException(typeof(TContext).Name);
        }

        List<string> pending;
        if (hasHistory)
        {
            pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        }
        else
        {
            // MigrateAsync reads the history table before creating it, which logs a failed query on a new database.
            await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(), cancellationToken);
            pending = db.Database.GetMigrations().ToList();
        }
        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database for {Context} ready (applied migrations: {Pending})",
            typeof(TContext).Name, pending.Count == 0 ? "none" : string.Join(", ", pending));
    }

    /// <summary>
    /// Queried directly: Npgsql's IHistoryRepository.ExistsAsync reports the history table as existing when it doesn't.
    /// </summary>
    private static async Task<(int Tables, bool HasHistory)> GetSchemaStateAsync(TContext db, CancellationToken cancellationToken)
    {
        var state = await db.Database
            .SqlQueryRaw<SchemaState>(
                "SELECT COUNT(*)::int AS \"Tables\", COALESCE(BOOL_OR(table_name = '__EFMigrationsHistory'), false) AS \"HasHistory\" " +
                "FROM information_schema.tables WHERE table_schema = current_schema()")
            .SingleAsync(cancellationToken);
        return (state.Tables, state.HasHistory);
    }

    private sealed record SchemaState(int Tables, bool HasHistory);
}

/// <summary>The database was created before its context had migrations, so it can't be upgraded in place.</summary>
public sealed class PreMigrationsDatabaseException(string context) : InvalidOperationException(
    $"The database for {context} was created before it used EF Core migrations and can't be upgraded in place. " +
    "In development, delete the Postgres data volume (docker volume ls | grep postgres, then docker volume rm <name>) " +
    "and restart the AppHost; the database is recreated and the catalog re-imported.");
