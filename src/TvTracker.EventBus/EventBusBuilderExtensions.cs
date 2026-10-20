using Microsoft.Extensions.DependencyInjection.Extensions;
using TvTracker.EventBus;

namespace Microsoft.Extensions.DependencyInjection;

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
