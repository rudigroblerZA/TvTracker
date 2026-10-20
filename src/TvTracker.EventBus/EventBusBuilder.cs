using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TvTracker.EventBus;

public interface IEventBusBuilder
{
    IServiceCollection Services { get; }
}

public sealed class EventBusBuilder(IServiceCollection services) : IEventBusBuilder
{
    public IServiceCollection Services { get; } = services;
}

/// <summary>Maps routing keys (event type names) to event types for the events a service subscribes to.</summary>
public sealed class EventBusSubscriptionInfo
{
    public Dictionary<string, Type> EventTypes { get; } = [];
}

public static class EventBusBuilderExtensions
{
    /// <summary>
    /// Subscribes <typeparamref name="THandler"/> to <typeparamref name="TEvent"/>.
    /// Handlers are resolved per message from a new scope, keyed by event type.
    /// </summary>
    public static IEventBusBuilder AddSubscription<TEvent, THandler>(this IEventBusBuilder builder)
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        builder.Services.TryAddTransient<THandler>();
        builder.Services.AddKeyedTransient<IIntegrationEventHandler>(typeof(TEvent),
            (sp, _) => new IntegrationEventHandlerAdapter<TEvent>(sp.GetRequiredService<THandler>()));

        builder.Services.Configure<EventBusSubscriptionInfo>(info =>
            info.EventTypes[EventName.For(typeof(TEvent))] = typeof(TEvent));

        return builder;
    }
}

public static class EventName
{
    /// <summary>The routing key used on the bus: the event type's name.</summary>
    public static string For(Type eventType) => eventType.Name;
}
