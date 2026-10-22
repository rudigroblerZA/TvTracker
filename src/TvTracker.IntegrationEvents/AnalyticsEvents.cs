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

/// <summary>
/// Shows whose DNA (see <see cref="DnaTraits"/>) changed in Analytics' hourly run, for Recommendations to match shows
/// by how they're made and how they play out, not only by what they're about. Like tags, every show's DNA is also
/// sent after Analytics starts and daily.
/// </summary>
public record ShowDnaChangedIntegrationEvent(IReadOnlyList<ShowDnaValues> Shows) : IntegrationEvent
{
    public const int MaxShows = 500;
}

/// <param name="Traits">Trait key to value, 0-1 (see <see cref="DnaTraits"/>). Traits without enough data are absent.</param>
public record ShowDnaValues(Guid ShowId, IReadOnlyDictionary<string, double> Traits);
