using TvTracker.EventBus;
using TvTracker.IntegrationEvents;

namespace TvTracker.Tests;

/// <summary>
/// The event bus as a list: what a module published, in order, once its outbox sent it on.
/// </summary>
public sealed class RecordingEventBus : IEventBus
{
    private readonly List<IntegrationEvent> published = [];

    public IReadOnlyList<IntegrationEvent> Published
    {
        get
        {
            lock (published)
            {
                return [.. published];
            }
        }
    }

    public IEnumerable<T> Of<T>() where T : IntegrationEvent => Published.OfType<T>();

    public void Clear()
    {
        lock (published)
        {
            published.Clear();
        }
    }

    public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        lock (published)
        {
            published.Add(@event);
        }
        return Task.CompletedTask;
    }
}
