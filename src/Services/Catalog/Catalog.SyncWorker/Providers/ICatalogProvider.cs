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

    /// <summary>Full details of one show, or null if the provider no longer has it.</summary>
    Task<ProviderShow?> GetShowAsync(string externalId, CancellationToken cancellationToken = default);
}

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
    IReadOnlyList<string> SimilarExternalIds);

public record ProviderSeason(int Number, string Name, string? Overview, DateTimeOffset? PremieresAtUtc);

/// <param name="AirsAtUtc">Air time in UTC; null when not yet scheduled.</param>
public record ProviderEpisode(
    string ExternalId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    string? Overview,
    DateTimeOffset? AirsAtUtc,
    int? RuntimeMinutes);

public record ProviderCastMember(string Name, string? Character, string? PhotoUrl);

public record ProviderWatchOption(string Region, string ProviderName, WatchProviderType Type, string? Url);
