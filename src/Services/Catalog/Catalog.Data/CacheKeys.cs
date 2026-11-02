namespace Catalog.Data;

/// <summary>
/// Cache keys shared by Catalog.API (reads) and Catalog.SyncWorker (invalidates on change).
/// </summary>
public static class CacheKeys
{
    public const string Trending = "catalog:trending";

    public static string Show(Guid showId, string region) => $"catalog:show:{showId}:{region}";

    public static string ShowTag(Guid showId) => $"catalog:show:{showId}";

    /// <summary>
    /// Marks a search term whose provider matches were imported recently (normalised: trimmed, lower case).
    /// </summary>
    public static string SearchImport(string query) => $"catalog:search-import:{query}";

    /// <summary>
    /// A history import's lookup of a provider show by TheTVDB id.
    /// </summary>
    public static string HistoryTvdb(string provider, string tvdbId) => $"catalog:history:tvdb:{provider}:{tvdbId}";

    /// <summary>
    /// A history import's lookup of a provider show by normalised title and year.
    /// </summary>
    public static string HistoryTitle(string provider, string titleKey, int? year) => $"catalog:history:title:{provider}:{titleKey}:{year}";
}
