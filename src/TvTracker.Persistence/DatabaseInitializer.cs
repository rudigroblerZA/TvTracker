using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TvTracker.Persistence;

/// <summary>
/// Creates or upgrades the schema at startup (and optionally seeds data) before the service starts taking requests.
/// Contexts with EF Core migrations are migrated; the others still use EnsureCreated, which never changes an
/// existing schema, until they move to migrations before the first production release.
/// A context that moves to migrations names its first migration as the <c>baseline</c>: a database EnsureCreated made
/// from the same model is then marked as having it, and migrated from there.
/// Only one service per database runs this (for catalogdb that's Catalog.API), and copies of that service starting
/// together take turns: the whole of it runs under a Postgres advisory lock, held on the context's connection.
/// </summary>
public sealed class DatabaseInitializer<TContext>(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializer<TContext>> logger,
    Func<TContext, IServiceProvider, CancellationToken, Task>? seed,
    string? baseline = null) : IHostedService
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

                // Running locally the AppHost creates each database; deployed, nothing has, and it's made here (as
                // MigrateAsync would, were there a connection to lock on first).
                var creator = db.GetService<IRelationalDatabaseCreator>();
                if (!await creator.ExistsAsync(cancellationToken))
                {
                    await creator.CreateAsync(cancellationToken);
                    logger.LogInformation("Created the database for {Context}", typeof(TContext).Name);
                }

                // Session-level, so it needs the one connection kept open until it's released; a second copy of the
                // service waits here and then finds nothing left to do.
                await db.Database.OpenConnectionAsync(cancellationToken);
                await db.Database.ExecuteSqlRawAsync(Lock, cancellationToken);
                try
                {
                    await InitialiseAsync(db, scope.ServiceProvider, cancellationToken);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(Unlock, CancellationToken.None);
                    await db.Database.CloseConnectionAsync();
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

    /// <summary>
    /// "TVTR" in ASCII, as a number. One key serves every service: advisory locks are per database, and each has its own.
    /// </summary>
    private const string AdvisoryLockKey = "1414943826";

    private const string Lock = "SELECT pg_advisory_lock(" + AdvisoryLockKey + ")";

    private const string Unlock = "SELECT pg_advisory_unlock(" + AdvisoryLockKey + ")";

    private async Task InitialiseAsync(TContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
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
            await seed(db, services, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task MigrateAsync(TContext db, CancellationToken cancellationToken)
    {
        // Checked first: asking a new database for its applied migrations logs failed queries for the missing history table.
        var (tables, hasHistory) = await GetSchemaStateAsync(db, cancellationToken);
        if (!hasHistory && tables > 0)
        {
            // Created by EnsureCreated before this context had migrations: there's no history to migrate from, unless
            // the context's first migration is that same schema.
            if (baseline is null || db.Database.GetMigrations().FirstOrDefault() is not { } first || !first.EndsWith($"_{baseline}", StringComparison.Ordinal))
            {
                throw new PreMigrationsDatabaseException(typeof(TContext).Name);
            }

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), cancellationToken);
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(first, ProductInfo.GetVersion())), cancellationToken);
            logger.LogInformation("Database for {Context} was made by EnsureCreated; marked as at migration {Baseline}", typeof(TContext).Name, first);
            hasHistory = true;
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

/// <summary>
/// The database was created before its context had migrations, so it can't be upgraded in place.
/// </summary>
public sealed class PreMigrationsDatabaseException(string context) : InvalidOperationException(
    $"The database for {context} was created before it used EF Core migrations and can't be upgraded in place. " +
    "In development, delete the Postgres data volume (docker volume ls | grep postgres, then docker volume rm <name>) " +
    "and restart the AppHost; the database is recreated and the catalog re-imported.");
