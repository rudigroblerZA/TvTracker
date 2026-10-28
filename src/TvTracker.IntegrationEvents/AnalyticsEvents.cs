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
/// against the season, and how many of our viewers who started it have finished it, for Notifications to tell the
/// show's followers. Sent on every hourly run for a week after it can first be given, each time with the same
/// <paramref name="VerdictId"/>, so a run that fails before publishing loses nothing and Notifications tells followers once.
/// </summary>
/// <param name="Verdict">One of <see cref="SeasonVerdicts"/>.</param>
/// <param name="OthersMedian">The other seasons' median rating; null for a first season.</param>
/// <param name="Finale">One of <see cref="FinaleVerdicts"/>; null when the finale has no rating.</param>
/// <param name="Started">Our users who watched any of its episodes; null under 5.</param>
/// <param name="Finished">Of those, who have watched the finale.</param>
/// <param name="VerdictId">The same for every send of one season's verdict (from the show and season); the event's own Id
/// differs each time.</param>
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
    int? Finished,
    Guid VerdictId = default) : IntegrationEvent;
