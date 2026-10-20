namespace TvTracker.EventBus;

/// <summary>
/// Base type for every event that crosses a service boundary.
/// Consumers use <see cref="Id"/> to ignore duplicates.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
