using Catalog.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.Tests;

[assembly: AssemblyFixture(typeof(PostgresServer))]

namespace Catalog.IntegrationTests;

/// <summary>
/// The catalog's database, migrated as the services migrate it (pgvector and the search extensions included), for one
/// test class. Tests in a class share it and keep apart by using their own shows and people.
/// </summary>
public sealed class CatalogDatabase(PostgresServer server) : IAsyncLifetime
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
    /// A context of its own, as each job run and request has.
    /// </summary>
    public CatalogContext Context() =>
        new(new DbContextOptionsBuilder<CatalogContext>().UseNpgsql(connectionString, npgsql => npgsql.UseVector()).Options);
}
