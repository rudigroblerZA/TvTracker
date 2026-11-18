using Microsoft.EntityFrameworkCore;
using Social.API.Data;
using TvTracker.Tests;

[assembly: AssemblyFixture(typeof(PostgresServer))]

namespace Social.IntegrationTests;

/// <summary>
/// Social's database, migrated as the service migrates it, for one test class. Tests in a class share it and keep
/// apart by using their own users.
/// </summary>
public sealed class SocialDatabase(PostgresServer server) : IAsyncLifetime
{
    private string connectionString = "";

    public async ValueTask InitializeAsync()
    {
        connectionString = await server.CreateDatabaseAsync();
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public SocialContext Context() => new(new DbContextOptionsBuilder<SocialContext>().UseNpgsql(connectionString).Options);
}
