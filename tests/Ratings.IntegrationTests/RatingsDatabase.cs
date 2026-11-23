using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using TvTracker.Tests;

[assembly: AssemblyFixture(typeof(PostgresServer))]

namespace Ratings.IntegrationTests;

/// <summary>
/// Ratings' database, migrated as the service migrates it, for one test class. Tests in a class share it and keep
/// apart by using their own users, shows and episodes.
/// </summary>
public sealed class RatingsDatabase(PostgresServer server) : IAsyncLifetime
{
    private string connectionString = "";

    public async ValueTask InitializeAsync()
    {
        connectionString = await server.CreateDatabaseAsync();
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public RatingsContext Context() => new(new DbContextOptionsBuilder<RatingsContext>().UseNpgsql(connectionString).Options);
}
