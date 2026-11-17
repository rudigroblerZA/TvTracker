using Npgsql;
using Testcontainers.PostgreSql;

namespace TvTracker.Tests;

/// <summary>
/// One real Postgres server for a test run, in a container: the image the AppHost runs (Postgres 18 with pgvector), so
/// unique indexes, constraint names and extensions behave as they do deployed. Each test class asks it for a database
/// of its own. Needs Docker, as running the app does.
/// </summary>
public sealed class PostgresServer : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg18-trixie").Build();

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    /// <summary>
    /// A new, empty database, and the connection string for it.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"create database \"{name}\"", connection);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}
