using Identity.API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TvTracker.Tests;

[assembly: AssemblyFixture(typeof(PostgresServer))]

namespace Identity.IntegrationTests;

/// <summary>
/// Identity's database, migrated as the service migrates it, for one test class. Tests in a class share it and keep
/// apart by using their own users.
/// </summary>
public sealed class IdentityDatabase(PostgresServer server) : IAsyncLifetime
{
    private string connectionString = "";

    public async ValueTask InitializeAsync()
    {
        connectionString = await server.CreateDatabaseAsync();
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// A context of its own, as each request has.
    /// </summary>
    public IdentityContext Context(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<IdentityContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);
}
