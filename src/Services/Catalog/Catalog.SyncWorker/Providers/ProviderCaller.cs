namespace Catalog.SyncWorker.Providers;

/// <summary>
/// Who a call to the catalog's provider is for: people waiting on it, or the background jobs that keep the catalog
/// fresh.
/// </summary>
public enum ProviderLane
{
    Background,
    Interactive
}

/// <summary>
/// The caller of the provider calls made in the current flow (an <see cref="AsyncLocal{T}"/>), set by each entry point
/// with <see cref="Begin"/>. It belongs to no one provider: the jobs that set it don't know which provider is behind
/// <see cref="ICatalogProvider"/>, and each provider reads it for its own purposes (TVmaze's limiter picks its lane by
/// it and counts usage under its name; Wikidata's lets a waiting interactive query go first). Calls made outside any
/// scope are background work named "other".
/// </summary>
public static class ProviderCaller
{
    private static readonly AsyncLocal<(string Name, ProviderLane Lane)?> current = new();

    public static (string Name, ProviderLane Lane) Current => current.Value ?? ("other", ProviderLane.Background);

    public static IDisposable Begin(string name, ProviderLane lane)
    {
        var previous = current.Value;
        current.Value = (name, lane);
        return new Scope(previous);
    }

    private sealed class Scope((string, ProviderLane)? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}
