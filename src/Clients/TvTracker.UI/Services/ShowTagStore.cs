using TvTracker.UI.Models;

namespace TvTracker.UI.Services;

/// <summary>
/// Show tags ("Bingeable", "Fan favorite", …) for show cards. Cards ask for their show's tags as they render; the
/// requests made while a page renders go to Analytics as one batch, and the results are kept for the session
/// (tags are recomputed hourly, so that's fresh enough). Signed-in users only, like the rest of Insights.
/// </summary>
public sealed class ShowTagStore(AnalyticsClient analytics, UserSession session)
{
    /// <summary>Tags shown on a card, in this order of preference; at most <see cref="MaxPerCard"/>.</summary>
    private static readonly string[] CardPriority =
        ["fan-favorite", "weekend-binge", "bingeable", "drops-at-once", "limited-series", "long-running", "quick-episodes"];

    private const int MaxPerCard = 2;
    private const int MaxPerRequest = 100;

    private readonly Dictionary<Guid, IReadOnlyList<ShowTag>> _tags = [];
    private readonly HashSet<Guid> _requested = [];
    private readonly HashSet<Guid> _pending = [];
    private readonly Lock _lock = new();
    private Task? _flush;

    /// <summary>Raised when tags arrive; cards re-render then.</summary>
    public event Action? Changed;

    /// <summary>The tags to show on a card, or none (not signed in, not loaded yet, or the show has none).</summary>
    /// <param name="preferred">A tag key to put first when the show has it, e.g. the tag search results are filtered by.</param>
    public IReadOnlyList<ShowTag> ForCard(Guid showId, string? preferred = null)
    {
        lock (_lock)
        {
            if (!_tags.TryGetValue(showId, out var tags))
            {
                return [];
            }

            // A weekend binge is bingeable too; show the more specific one (unless bingeable is the one asked for).
            var keys = tags.Select(t => t.Key).ToHashSet();
            return tags
                .Where(t => t.Key == preferred
                    || (CardPriority.Contains(t.Key) && !(t.Key == "bingeable" && keys.Contains("weekend-binge"))))
                .OrderBy(t => t.Key == preferred ? -1 : Array.IndexOf(CardPriority, t.Key))
                .Take(MaxPerCard)
                .ToList();
        }
    }

    /// <summary>Asks for a show's tags; they arrive with the rest of the page's in one request.</summary>
    public void Request(Guid showId)
    {
        lock (_lock)
        {
            if (!_requested.Add(showId))
            {
                return;
            }

            _pending.Add(showId);
            _flush ??= FlushSoonAsync();
        }
    }

    private async Task FlushSoonAsync()
    {
        // Let the rest of the page's cards ask first.
        await Task.Delay(50);

        List<Guid> ids;
        lock (_lock)
        {
            ids = [.. _pending];
            _pending.Clear();
            _flush = null;
        }

        try
        {
            await session.EnsureLoadedAsync();
            if (!session.IsSignedIn)
            {
                return;
            }

            var loaded = false;
            foreach (var chunk in ids.Chunk(MaxPerRequest))
            {
                if (await analytics.GetTagsAsync(chunk) is { } tags)
                {
                    lock (_lock)
                    {
                        foreach (var (id, showTags) in tags)
                        {
                            _tags[id] = showTags;
                        }
                    }
                    loaded = true;
                }
            }

            if (loaded)
            {
                Changed?.Invoke();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or SignInRequiredException)
        {
            // Tags are extra: cards work without them. Let a later page ask again.
            lock (_lock)
            {
                _requested.ExceptWith(ids);
            }
        }
    }
}
