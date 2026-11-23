using Microsoft.EntityFrameworkCore;
using Tracking.Data;
using TvTracker.Tests;

[assembly: AssemblyFixture(typeof(PostgresServer))]

namespace Tracking.IntegrationTests;

/// <summary>
/// Tracking's database, migrated as the service migrates it, for one test class. Tests in a class share it and keep
/// apart by using their own users and shows.
/// </summary>
public sealed class TrackingDatabase(PostgresServer server) : IAsyncLifetime
{
    private string connectionString = "";

    public async ValueTask InitializeAsync()
    {
        connectionString = await server.CreateDatabaseAsync();
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public TrackingContext Context() => new(new DbContextOptionsBuilder<TrackingContext>().UseNpgsql(connectionString).Options);
}
