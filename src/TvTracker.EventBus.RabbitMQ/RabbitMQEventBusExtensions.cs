using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TvTracker.EventBus;
using TvTracker.EventBus.RabbitMQ;

namespace Microsoft.Extensions.Hosting;

public static class RabbitMQEventBusExtensions
{
    /// <summary>
    /// Registers the RabbitMQ event bus. The queue name defaults to the application name, so each service
    /// gets its own queue. Add subscriptions with <c>.AddSubscription&lt;TEvent, THandler&gt;()</c>.
    /// Tracing for the bus ("TvTracker.EventBus") is enabled in ServiceDefaults.
    /// </summary>
    public static IEventBusBuilder AddRabbitMQEventBus(this IHostApplicationBuilder builder, string connectionName)
    {
        builder.Services.Configure<RabbitMQEventBusOptions>(options =>
        {
            options.ConnectionName = connectionName;
            if (string.IsNullOrWhiteSpace(options.SubscriptionClientName))
            {
                options.SubscriptionClientName = builder.Environment.ApplicationName;
            }
        });

        builder.Services.TryAddSingleton<RabbitMQEventBus>();
        builder.Services.TryAddSingleton<IEventBus>(sp => sp.GetRequiredService<RabbitMQEventBus>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RabbitMQEventBus>());

        builder.Services.AddHealthChecks()
            .AddCheck<RabbitMQEventBusHealthCheck>("eventbus");

        return new EventBusBuilder(builder.Services);
    }
}
