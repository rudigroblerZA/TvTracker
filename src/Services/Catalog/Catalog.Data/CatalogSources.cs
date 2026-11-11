using Microsoft.Extensions.Configuration;

namespace Catalog.Data;

/// <summary>
/// The provider the catalog is filled from, as stored in <see cref="Show.Source"/>: <c>Catalog:Provider</c>, set by the
/// AppHost on the sync worker and Catalog.API alike, TVmaze when unset.
/// </summary>
public static class CatalogSources
{
    public const string TvMaze = "tvmaze";
    public const string Wikidata = "wikidata";
    public const string Tvdb = "tvdb";
    public const string Mock = "mock";

    public static string Configured(IConfiguration configuration) =>
        configuration["Catalog:Provider"]?.Trim().ToLowerInvariant() switch
        {
            Wikidata => Wikidata,
            Tvdb => Tvdb,
            Mock => Mock,
            _ => TvMaze
        };
}
