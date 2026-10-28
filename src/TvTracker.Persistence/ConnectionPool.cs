using System.Data.Common;

namespace TvTracker.Persistence;

/// <summary>
/// The connection budget every service's Npgsql pool keeps to. All the services' databases live on one Postgres
/// server, whose <c>max_connections</c> they share, while Npgsql's defaults let each pool grow to 100 and keep idle
/// connections for 5 minutes.
/// <para>
/// Without a cap, the services that had been busy held their idle connections, and under load (80 users) the server
/// refused new ones ("too many clients", about 20 a second). Npgsql treats that as transient, so EF's retry strategy
/// backed off and retried for up to 30 s: Tracking, the service that needed new connections last, answered in 2-30 s,
/// reads included. Capped, a busy service queues for a connection in its own pool for milliseconds instead.
/// </para>
/// <para>
/// Keep <see cref="MaxPoolSize"/> × the database connections' owners (10 services, more with replicas) under the
/// server's <c>max_connections</c> (Postgres' default of 100 in development), with room for migrations and tools.
/// Under that load with the cap, no database held more than 12 connections (Catalog's and Analytics' two processes
/// each), and samples never caught more than one query running per database.
/// </para>
/// </summary>
public static class ConnectionPool
{
    public const int MaxPoolSize = 8;

    /// <summary>Seconds an idle connection stays in the pool; Npgsql's default is 300.</summary>
    public const int IdleLifetimeSeconds = 60;

    /// <summary>The connection string with the pool limits added, unless it already sets them.</summary>
    public static string? Limit(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return connectionString;
        }

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!builder.ContainsKey("Maximum Pool Size") && !builder.ContainsKey("Max Pool Size"))
        {
            builder["Maximum Pool Size"] = MaxPoolSize;
        }
        if (!builder.ContainsKey("Connection Idle Lifetime"))
        {
            builder["Connection Idle Lifetime"] = IdleLifetimeSeconds;
        }
        return builder.ConnectionString;
    }
}
