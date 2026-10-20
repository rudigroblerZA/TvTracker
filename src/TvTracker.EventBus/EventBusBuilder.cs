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

public static class EventName
{
    /// <summary>The routing key used on the bus: the event type's name.</summary>
    public static string For(Type eventType) => eventType.Name;
}
