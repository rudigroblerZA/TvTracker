using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Catalog.SyncWorker (on behalf of Catalog)

public enum ShowStatus
{
    Upcoming,
    Airing,
    Renewed,
    Ended,
    Cancelled
}

/// <summary>A show was imported into the catalog for the first time.</summary>
/// <param name="ExternalRating">The provider's average rating (0-10), if any.</param>
/// <param name="Cast">The cast with character ids, in billing order. Null in events published before it was added.</param>
/// <param name="Creatives">Its creators and executive producers, creators first. Null in events published before it was added.</param>
public record ShowAddedIntegrationEvent(
    Guid ShowId,
    string Title,
    string? PosterUrl,
    ShowStatus Status,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> CastNames,
    IReadOnlyList<Guid> SimilarShowIds,
    double Popularity,
    double? ExternalRating = null,
    IReadOnlyList<CastCredit>? Cast = null,
    IReadOnlyList<CreativeCredit>? Creatives = null) : IntegrationEvent;

/// <summary>A show's details changed (title, poster, genres, keywords, cast, creatives, similar shows, rating).</summary>
public record ShowUpdatedIntegrationEvent(
    Guid ShowId,
    string Title,
    string? PosterUrl,
    ShowStatus Status,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> CastNames,
    IReadOnlyList<Guid> SimilarShowIds,
    double Popularity,
    double? ExternalRating = null,
    IReadOnlyList<CastCredit>? Cast = null,
    IReadOnlyList<CreativeCredit>? Creatives = null) : IntegrationEvent;

/// <param name="CharacterExternalIds">
/// The provider's ids for the characters played (empty if it has none). The same id in two shows means a shared character.
/// </param>
/// <param name="PersonId">The catalog's id for the person (their actor page); null in events published before it was added.</param>
public record CastCredit(string Name, string? Character, IReadOnlyList<string> CharacterExternalIds, Guid? PersonId = null);

/// <param name="Roles">"Creator", "Developer" and/or "Executive Producer".</param>
public record CreativeCredit(Guid PersonId, string Name, IReadOnlyList<string> Roles);

/// <summary>
/// Career figures behind the camera (from the catalog provider, across all its shows) for people who make shows.
/// Sent as the sync worker looks them up, and with a catalog replay.
/// </summary>
public record PeopleCareersUpdatedIntegrationEvent(IReadOnlyList<PersonCareer> People) : IntegrationEvent
{
    public const int MaxPeople = 500;
}

/// <param name="Shows">Shows they're credited on behind the camera.</param>
/// <param name="Created">Shows they created or developed.</param>
/// <param name="ExecutiveProduced">Shows they were an executive producer on.</param>
public record PersonCareer(Guid PersonId, string Name, int Shows, int Created, int ExecutiveProduced);

/// <summary>
/// Episodes of one show were scheduled, or their details or air times changed. Times are UTC.
/// One event per sync of a show, split into batches of at most <see cref="MaxEpisodes"/>,
/// so importing a show with thousands of episodes publishes a handful of events rather than thousands.
/// </summary>
public record EpisodesScheduledIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    IReadOnlyList<ScheduledEpisode> Episodes) : IntegrationEvent
{
    public const int MaxEpisodes = 500;
}

public record ScheduledEpisode(
    Guid EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    DateTimeOffset? AirsAtUtc,
    int? RuntimeMinutes,
    double? ExternalRating = null);

/// <summary>
/// One episode scheduled or changed. No longer published (replaced by <see cref="EpisodesScheduledIntegrationEvent"/>);
/// consumers keep handling it so messages published before the change still apply. Remove once none can be pending.
/// </summary>
public record EpisodeScheduledIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    Guid EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    DateTimeOffset? AirsAtUtc,
    int? RuntimeMinutes) : IntegrationEvent;

/// <summary>The episode's air time has passed.</summary>
public record EpisodeAiredIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    Guid EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title) : IntegrationEvent;

public record SeasonAnnouncedIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    int SeasonNumber,
    DateTimeOffset? PremieresAtUtc) : IntegrationEvent;

public record ShowStatusChangedIntegrationEvent(
    Guid ShowId,
    string ShowTitle,
    ShowStatus OldStatus,
    ShowStatus NewStatus) : IntegrationEvent;
