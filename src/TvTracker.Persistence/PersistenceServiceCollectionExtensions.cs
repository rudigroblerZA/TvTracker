using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TvTracker.Persistence;

namespace Microsoft.Extensions.Hosting;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers the outbox for <typeparamref name="TContext"/> and the background publisher that retries it.</summary>
    public static IServiceCollection AddIntegrationEventOutbox<TContext>(this IServiceCollection services)
        where TContext : DbContext, IIntegrationEventLogDbContext
    {
        services.AddScoped<IIntegrationEventOutbox, IntegrationEventOutbox<TContext>>();
        services.AddHostedService<OutboxPublisher>();
        return services;
    }

    /// <summary>Creates the database schema at startup, then runs <paramref name="seed"/> if given.</summary>
    public static IServiceCollection AddDatabaseInitializer<TContext>(
        this IServiceCollection services,
        Func<TContext, IServiceProvider, CancellationToken, Task>? seed = null)
        where TContext : DbContext
    {
        services.AddHostedService(sp => new DatabaseInitializer<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<DatabaseInitializer<TContext>>>(),
            seed));
        return services;
    }
}
