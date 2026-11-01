using Catalog.Data;
using TvTracker.IntegrationEvents;

namespace Catalog.SyncWorker.Providers;

/// <summary>
/// Source of show data. Implementations: TvMazeCatalogProvider (default) and MockCatalogProvider.
/// A real provider should also respect the provider's rate limits.
/// </summary>
public interface ICatalogProvider
{
    /// <summary>Stored as <see cref="Show.Source"/>.</summary>
    string Name { get; }

    /// <summary>
    /// External ids of shows to import proactively (for a real provider: popular and currently airing shows).
    /// Only ids, so the synchronizer fetches full details just for the ones it doesn't have yet.
    /// </summary>
    Task<IReadOnlyList<string>> GetFeaturedShowIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// External ids of the provider's <paramref name="count"/> most popular shows, most popular first. May page through
    /// the provider's whole index; <paramref name="onPage"/> is called with the number of pages read so far.
    /// </summary>
    Task<IReadOnlyList<string>> GetPopularShowIdsAsync(int count, Action<int>? onPage = null, CancellationToken cancellationToken = default);

    /// <summary>External ids of shows matching a title search, best match first.</summary>
    Task<IReadOnlyList<string>> SearchShowIdsAsync(string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The provider's shows for a title search, best match first, as summaries (no episodes or cast), for finding a
    /// show's namesakes.
    /// </summary>
    Task<IReadOnlyList<ProviderShowSummary>> SearchShowsAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Full details of one show, or null if the provider no longer has it.</summary>
    Task<ProviderShow?> GetShowAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>A person's whole career behind the camera, from the provider; null when it has nothing for them.</summary>
    Task<ProviderCareer?> GetCareerAsync(string personExternalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The shows a person is in the main cast of, across the provider, without appearances as themselves; null when
    /// the provider doesn't know the person.
    /// </summary>
    Task<IReadOnlyList<ProviderActingRole>?> GetCastCreditsAsync(string personExternalId, CancellationToken cancellationToken = default);
}

/// <param name="CastMissing">The provider failed to return the cast this time (an outage): Cast is empty, and the catalog keeps the cast it has.</param>
/// <param name="ImagesMissing">Likewise for images: BackdropUrl and BannerUrl are null, and the catalog keeps the ones it has.</param>
/// <param name="BannerUrl">A thin wide banner (with the show's logo), when the provider has one.</param>
public record ProviderShow(
    string ExternalId,
    string Title,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    DateOnly? FirstAirDate,
    ShowStatus Status,
    string? Network,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Keywords,
    double Popularity,
    IReadOnlyList<ProviderSeason> Seasons,
    IReadOnlyList<ProviderEpisode> Episodes,
    IReadOnlyList<ProviderCastMember> Cast,
    IReadOnlyList<ProviderWatchOption> WatchOptions,
    IReadOnlyList<string> SimilarExternalIds,
    double? Rating,
    IReadOnlyList<ProviderCreative>? Creatives = null,
    bool CastMissing = false,
    bool ImagesMissing = false,
    string? BannerUrl = null);

/// <summary>Someone who made the show: its creators and executive producers.</summary>
/// <param name="Roles">"Creator", "Developer" and/or "Executive Producer".</param>
public record ProviderCreative(string Name, string? PhotoUrl, IReadOnlyList<string> Roles, ProviderPerson? Person);

/// <param name="Shows">Shows they're credited on behind the camera.</param>
/// <param name="Created">Shows they created or developed.</param>
/// <param name="ExecutiveProduced">Shows they were an executive producer on.</param>
/// <param name="NotableShows">Their best-known shows (the provider's most popular), at most 10.</param>
public record ProviderCareer(int Shows, int Created, int ExecutiveProduced, IReadOnlyList<string> NotableShows);

/// <param name="Character">The character, or several joined with " / " when they play more than one.</param>
public record ProviderActingRole(
    string ShowExternalId, string Title, string? PosterUrl, ShowStatus Status, DateOnly? Premiered, double Popularity, double? Rating,
    string? Character, bool Voice);

/// <param name="Kind">The provider's type, lower case ("scripted", "animation", "reality", …), as a show's kind keyword.</param>
/// <param name="Country">The network's or streaming service's country code ("GB", "US").</param>
public record ProviderShowSummary(
    string ExternalId, string Title, string? PosterUrl, DateOnly? Premiered, DateOnly? Ended, string? Network, string? Country, string? Language,
    string? Kind, double Popularity, double? Rating);

public record ProviderSeason(int Number, string Name, string? Overview, DateTimeOffset? PremieresAtUtc);

/// <param name="AirsAtUtc">Air time in UTC; null when not yet scheduled.</param>
/// <param name="Rating">The provider's average rating (0-10); null when it has none.</param>
public record ProviderEpisode(
    string ExternalId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    string? Overview,
    DateTimeOffset? AirsAtUtc,
    int? RuntimeMinutes,
    double? Rating);

/// <param name="CharacterExternalIds">The provider's ids for the characters played; empty when it has none.</param>
/// <param name="Person">Details about the actor; null when the provider has none.</param>
public record ProviderCastMember(string Name, string? Character, string? PhotoUrl, IReadOnlyList<string> CharacterExternalIds,
    ProviderPerson? Person = null);

public record ProviderPerson(string ExternalId, DateOnly? Birthday, DateOnly? Deathday, string? Gender, string? Country);

public record ProviderWatchOption(string Region, string ProviderName, WatchProviderType Type, string? Url);
