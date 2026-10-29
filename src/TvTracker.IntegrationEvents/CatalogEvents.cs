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
/// <param name="Network">Its network or streaming service ("HBO", "Netflix"), if any. Null in events published before it
/// was added too, so <paramref name="NetworkKnown"/> tells the two apart.</param>
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
    IReadOnlyList<CreativeCredit>? Creatives = null,
    string? Network = null,
    bool NetworkKnown = false) : IntegrationEvent;

/// <summary>A show's details changed (title, poster, genres, keywords, cast, creatives, similar shows, rating, network).</summary>
/// <param name="Network">As in <see cref="ShowAddedIntegrationEvent"/>.</param>
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
    IReadOnlyList<CreativeCredit>? Creatives = null,
    string? Network = null,
    bool NetworkKnown = false) : IntegrationEvent;

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
/// Shows whose nanogenres (fine-grained genres the sync worker discovers, like "Sports comedy") changed. Sent in full
/// after the worker starts, so a lost event heals itself. A show that fits none is sent with an empty list.
/// </summary>
public record ShowNanogenresChangedIntegrationEvent(IReadOnlyList<ShowNanogenreNames> Shows) : IntegrationEvent
{
    public const int MaxShows = 500;
}

/// <param name="Nanogenres">Names, most specific (smallest) first.</param>
public record ShowNanogenreNames(Guid ShowId, IReadOnlyList<string> Nanogenres);

/// <summary>
/// Actors' roles across the catalog provider (not just this catalog), for actors in catalog shows: the data behind the
/// reunion radar. Each person's list replaces the one sent before. Sent as the sync worker looks them up (monthly per
/// person), and with a catalog replay.
/// </summary>
public record PeopleCastCreditsUpdatedIntegrationEvent(IReadOnlyList<PersonCastCredits> People) : IntegrationEvent
{
    public const int MaxPeople = 100;
}

public record PersonCastCredits(Guid PersonId, string Name, IReadOnlyList<ActingRole> Roles);

/// <summary>
/// One show an actor is in the main cast of. The provider lists main casts only; appearances on a show as themselves
/// (talk shows, documentaries) are left out.
/// </summary>
/// <param name="ShowExternalId">The provider's id for the show.</param>
/// <param name="ShowId">The catalog's id, when the show is in the catalog.</param>
/// <param name="Character">The character, or several joined with " / ".</param>
/// <param name="Voice">A voice role (animation).</param>
public record ActingRole(
    string ShowExternalId,
    Guid? ShowId,
    string Title,
    string? PosterUrl,
    ShowStatus Status,
    DateOnly? Premiered,
    double Popularity,
    double? Rating,
    string? Character,
    bool Voice);

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

/// <summary>
/// A crossover event across a universe's shows starting soon (Catalog.API's crossover alerts). Sent every hour while
/// it's ahead, so a lost one heals; consumers use <paramref name="CrossoverId"/> as its identity, so followers hear once.
/// </summary>
/// <param name="CrossoverId">Stable for the event (its universe, name and year).</param>
/// <param name="UniverseId">The universe's hub show, which addresses its pages.</param>
/// <param name="Parts">One per show's episode, in air order.</param>
public record CrossoverScheduledIntegrationEvent(
    Guid CrossoverId,
    string Name,
    Guid UniverseId,
    string UniverseName,
    IReadOnlyList<CrossoverPartItem> Parts) : IntegrationEvent;

public record CrossoverPartItem(Guid ShowId, string ShowTitle, Guid EpisodeId, int SeasonNumber, int EpisodeNumber, DateTimeOffset AirsAtUtc);
