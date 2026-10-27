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
        builder.Services.AddDbContextPool<CatalogContext>(options => options.UseNpgsql(ConnectionPool.Limit(connectionString), npgsql => npgsql.UseVector()));
        builder.EnrichNpgsqlDbContext<CatalogContext>();
        return builder;
    }
}
