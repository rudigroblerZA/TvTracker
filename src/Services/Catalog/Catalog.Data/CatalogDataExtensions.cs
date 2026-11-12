using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TvTracker.Persistence;

namespace Catalog.Data;

public static class CatalogDataExtensions
{
    /// <summary>
    /// Registers <see cref="CatalogContext"/> for the "catalogdb" connection with pgvector types enabled, plus Aspire's
    /// retries, health check and telemetry (which AddNpgsqlDbContext would add, but it can't enable plugins).
    /// </summary>
    public static IHostApplicationBuilder AddCatalogDbContext(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("catalogdb")
            ?? throw new InvalidOperationException("The \"catalogdb\" connection string is missing.");
        builder.Services.AddDbContextPool<CatalogContext>(options => options.UseNpgsql(ConnectionPool.Limit(WithSearchSettings(connectionString)), npgsql => npgsql.UseVector()));
        builder.EnrichNpgsqlDbContext<CatalogContext>();
        return builder;
    }

    /// <summary>
    /// Every connection starts with search's near-spelling cutoff (<see cref="CatalogContext.NearSpellingThreshold"/>)
    /// as pg_trgm's word-similarity threshold, which the indexed <c>&lt;%</c> operator reads. On the connection rather
    /// than the database, so it holds from the first query and needs no rights over the database.
    /// </summary>
    internal static string WithSearchSettings(string connectionString)
    {
        var settings = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        var threshold = CatalogContext.NearSpellingThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings.Options = $"{settings.Options} -c pg_trgm.word_similarity_threshold={threshold}".Trim();
        return settings.ConnectionString;
    }
}
