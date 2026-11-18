using Analytics.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.Tests;

[assembly: AssemblyFixture(typeof(PostgresServer))]

namespace Analytics.IntegrationTests;

/// <summary>
/// Analytics' database, migrated as the service migrates it, for one test class. Tests in a class share it and keep
/// apart by using their own shows.
/// </summary>
public sealed class AnalyticsDatabase(PostgresServer server) : IAsyncLifetime
{
    private string connectionString = "";

    public async ValueTask InitializeAsync()
    {
        connectionString = await server.CreateDatabaseAsync();
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public AnalyticsContext Context() => new(new DbContextOptionsBuilder<AnalyticsContext>().UseNpgsql(connectionString).Options);
}
