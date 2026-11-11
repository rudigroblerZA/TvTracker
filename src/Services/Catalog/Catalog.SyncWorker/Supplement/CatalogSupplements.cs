using Catalog.Data;
using Catalog.SyncWorker.Providers;

namespace Catalog.SyncWorker.Supplement;

/// <summary>
/// Filling what the provider leaves empty from what Wikidata has for the show (<see cref="ShowSupplement"/>).
/// </summary>
public static class CatalogSupplements
{
    /// <summary>
    /// The Wikidata property holding each provider's id for a show; a provider not listed can't be supplemented.
    /// </summary>
    public static string? IdProperty(string source) => source switch
    {
        CatalogSources.TvMaze => "P8600",
        CatalogSources.Tvdb => "P4835",
        _ => null
    };

    /// <summary>
    /// The show with its empty fields filled from the supplement; a field the provider has is never replaced.
    /// </summary>
    public static ProviderShow Fill(ProviderShow show, ShowSupplement? supplement) =>
        supplement is null
            ? show
            : show with
            {
                Overview = string.IsNullOrWhiteSpace(show.Overview) ? supplement.Overview ?? show.Overview : show.Overview,
                Network = string.IsNullOrWhiteSpace(show.Network) ? supplement.Network ?? show.Network : show.Network,
                FirstAirDate = show.FirstAirDate ?? supplement.FirstAirDate,
                PosterUrl = string.IsNullOrWhiteSpace(show.PosterUrl) ? supplement.PosterUrl ?? show.PosterUrl : show.PosterUrl
            };

    /// <summary>
    /// Whether the supplement has something for a field the show lacks, so a refresh would change the show.
    /// </summary>
    public static bool Helps(ShowSupplement supplement, string? overview, string? network, DateOnly? firstAirDate, string? posterUrl) =>
        (string.IsNullOrWhiteSpace(overview) && supplement.Overview is not null)
        || (string.IsNullOrWhiteSpace(network) && supplement.Network is not null)
        || (firstAirDate is null && supplement.FirstAirDate is not null)
        || (string.IsNullOrWhiteSpace(posterUrl) && supplement.PosterUrl is not null);
}
