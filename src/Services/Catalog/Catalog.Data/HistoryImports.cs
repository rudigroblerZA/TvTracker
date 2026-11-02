namespace Catalog.Data;

/// <summary>
/// The contract between Catalog.API and the sync worker for Bring your history: the shows of someone's export to find
/// (and import when missing), and what became of each.
/// </summary>
public static class HistoryImports
{
    /// <summary>Shows a call, since each one the catalog lacks is two provider calls on the shared limiter.</summary>
    public const int MaxShows = 10;
}

/// <param name="Key">The caller's handle for the show, given back unchanged.</param>
/// <param name="TvdbId">TheTVDB's id, when the export has one (TV Time's do).</param>
/// <param name="Year">The year the show started, when known, to tell namesakes apart.</param>
public record ResolveShowRequest(string Key, string? TvdbId, string? Title, int? Year);

public record ResolveShowsRequest(IReadOnlyList<ResolveShowRequest> Shows);

/// <param name="ExternalId">The provider's id, now in the catalog; null when the show couldn't be found.</param>
/// <param name="Outcome">"found" (already in the catalog), "imported", "not-found" or "failed" (the provider erred; try again later).</param>
public record ResolvedShow(string Key, string? ExternalId, string Outcome);

public record ResolveShowsResult(IReadOnlyList<ResolvedShow> Shows);
