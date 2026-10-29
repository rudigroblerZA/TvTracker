using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddHostedService<IntegrationEventRetention>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Creates or migrates the database schema at startup, then runs <paramref name="seed"/> if given.</summary>
    /// <param name="baseline">
    /// For a context that moved from EnsureCreated to migrations: the name of its first migration, made from the same
    /// model, so existing databases are marked as having it instead of refusing to start.
    /// </param>
    public static IServiceCollection AddDatabaseInitializer<TContext>(
        this IServiceCollection services,
        Func<TContext, IServiceProvider, CancellationToken, Task>? seed = null,
        string? baseline = null)
        where TContext : DbContext
    {
        services.AddHostedService(sp => new DatabaseInitializer<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<DatabaseInitializer<TContext>>>(),
            seed,
            baseline));
        return services;
    }
}
