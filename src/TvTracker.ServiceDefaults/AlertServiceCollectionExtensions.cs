using Microsoft.Extensions.DependencyInjection.Extensions;
using TvTracker;

namespace Microsoft.Extensions.DependencyInjection;

public static class AlertServiceCollectionExtensions
{
    /// <summary>
    /// Adds an alert check; the first one also adds the monitor and the "alerts" health check.
    /// </summary>
    public static IServiceCollection AddAlert<TCheck>(this IServiceCollection services) where TCheck : class, IAlertCheck
    {
        services.AddSingleton<IAlertCheck, TCheck>();
        if (services.Any(d => d.ServiceType == typeof(AlertMonitor)))
        {
            return services;
        }
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AlertMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<AlertMonitor>());
        services.AddHealthChecks().AddCheck<AlertsHealthCheck>("alerts");
        return services;
    }
}
