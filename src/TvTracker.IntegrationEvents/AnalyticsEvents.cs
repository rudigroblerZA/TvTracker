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

/// <summary>
/// A season finished airing a few days ago, and how it did: its rating against the show's other seasons, its finale
/// against the season, and how many of our viewers who started it have finished it. Sent once per season, for
/// Notifications to tell the show's followers. Not re-sent after start, so a lost one is lost (it's news, not state).
/// </summary>
/// <param name="Verdict">best-yet, above, in-line, below, weakest-yet or first-season.</param>
/// <param name="OthersMedian">The other seasons' median rating; null for a first season.</param>
/// <param name="Finale">strong, in-line or weak; null when the finale has no rating.</param>
/// <param name="Started">Our users who watched any of its episodes; null under 5.</param>
/// <param name="Finished">Of those, who have watched the finale.</param>
public record SeasonVerdictIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    int SeasonNumber,
    string Verdict,
    double Rating,
    double? OthersMedian,
    string? Finale,
    double? FinaleRating,
    int? Started,
    int? Finished) : IntegrationEvent;
