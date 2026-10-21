using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Analytics.Worker

/// <summary>
/// Shows whose tags ("bingeable", "fan-favorite", …) changed in Analytics' hourly run, so the catalog can filter search
/// by them. Analytics also sends every show's tags once after it starts and daily, so a lost event heals itself.
/// </summary>
public record ShowTagsChangedIntegrationEvent(IReadOnlyList<ShowTagKeys> Shows) : IntegrationEvent
{
    /// <summary>Shows per event; a full send is split into several.</summary>
    public const int MaxShows = 500;
}

/// <param name="Tags">The show's tag keys now; empty when it has none.</param>
public record ShowTagKeys(Guid ShowId, IReadOnlyList<string> Tags);
