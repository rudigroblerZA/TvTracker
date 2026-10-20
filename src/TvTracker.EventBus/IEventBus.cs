namespace TvTracker.EventBus;

public interface IEventBus
{
    Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handles one event type. A class may handle several event types by implementing this interface more than once.
/// </summary>
public interface IIntegrationEventHandler<in TEvent> where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>Non-generic handler the bus dispatches to; created by <see cref="EventBusBuilderExtensions.AddSubscription{TEvent, THandler}"/>.</summary>
public interface IIntegrationEventHandler
{
    Task HandleAsync(IntegrationEvent @event, CancellationToken cancellationToken = default);
}

internal sealed class IntegrationEventHandlerAdapter<TEvent>(IIntegrationEventHandler<TEvent> inner) : IIntegrationEventHandler
    where TEvent : IntegrationEvent
{
    public Task HandleAsync(IntegrationEvent @event, CancellationToken cancellationToken = default) =>
        inner.HandleAsync((TEvent)@event, cancellationToken);
}
