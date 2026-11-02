using System.Text.RegularExpressions;
using Catalog.SyncWorker.Providers;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalog.SyncWorker;

/// <param name="Key">The caller's handle for the show, given back unchanged.</param>
/// <param name="TvdbId">TheTVDB's id, when the export has one (TV Time's do).</param>
/// <param name="Year">The year the show started, when known, to tell namesakes apart.</param>
public record ResolveShowRequest(string Key, string? TvdbId, string? Title, int? Year);

public record ResolveShowsRequest(IReadOnlyList<ResolveShowRequest> Shows);

/// <param name="ExternalId">The provider's id, now in the catalog; null when the show couldn't be found.</param>
/// <param name="Outcome">"found" (already in the catalog), "imported", "not-found" or "failed" (the provider erred; try again later).</param>
public record ResolvedShow(string Key, string? ExternalId, string Outcome);

public record ResolveShowsResult(IReadOnlyList<ResolvedShow> Shows);

/// <summary>
/// Finds the provider's show for each entry of someone's history export and makes sure the catalog has it, for Bring
/// your history. A TheTVDB id is looked up directly (<see cref="ICatalogProvider.LookUpShowByTvdbAsync"/>); otherwise the
/// title is searched and taken only when a result's title matches it (and its year, when given, within a year), since
/// the first search result for "Ghosts" or "The Office" is a coin toss. Lookups are cached for <see cref="CacheFor"/>
/// (misses too, for a day), so a second import of the same export costs no provider calls for lookups.
/// </summary>
public sealed partial class HistoryResolver(ICatalogProvider provider, CatalogSynchronizer synchronizer, HybridCache cache, ILogger<HistoryResolver> logger)
{
    public const int MaxShows = 10;
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissCacheFor = TimeSpan.FromDays(1);

    public async Task<ResolveShowsResult> ResolveAsync(ResolveShowsRequest request, CancellationToken ct)
    {
        var results = new List<ResolvedShow>();
        foreach (var show in request.Shows.Take(MaxShows))
        {
            try
            {
                var externalId = await FindAsync(show, ct);
                if (externalId is null)
                {
                    results.Add(new ResolvedShow(show.Key, null, "not-found"));
                    continue;
                }
                var imported = await synchronizer.ImportShowAsync(externalId, ct);
                results.Add(new ResolvedShow(show.Key, externalId, imported.Outcome == ImportOutcome.Imported ? "imported" : "found"));
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Couldn't resolve {Title} (TVDB {TvdbId}) for a history import", show.Title, show.TvdbId);
                results.Add(new ResolvedShow(show.Key, null, "failed"));
            }
        }
        return new ResolveShowsResult(results);
    }

    private async Task<string?> FindAsync(ResolveShowRequest show, CancellationToken ct)
    {
        if (show.TvdbId is { Length: > 0 } tvdb)
        {
            var found = await CachedAsync($"history:tvdb:{provider.Name}:{tvdb}", () => provider.LookUpShowByTvdbAsync(tvdb, ct), ct);
            if (found is not null)
            {
                return found;
            }
        }
        if (show.Title is not { Length: > 1 } title)
        {
            return null;
        }
        var key = $"history:title:{provider.Name}:{Normalise(title)}:{show.Year}";
        return await CachedAsync(key, async () =>
        {
            var results = await provider.SearchShowsAsync(title, ct);
            var wanted = Normalise(title);
            bool YearFits(ProviderShowSummary s) => show.Year is not { } y || s.Premiered is not { } p || Math.Abs(p.Year - y) <= 1;
            // An exact title first, then a title that only differs by a trailing year or country, then a year match alone.
            var match = results.FirstOrDefault(s => Normalise(s.Title) == wanted && YearFits(s))
                ?? results.FirstOrDefault(s => TrailingParenthesis().Replace(Normalise(s.Title), "").Trim() == wanted && YearFits(s))
                ?? (show.Year is not null ? results.FirstOrDefault(s => s.Premiered?.Year == show.Year && Normalise(s.Title).Contains(wanted)) : null);
            return match?.ExternalId;
        }, ct);
    }

    private async Task<string?> CachedAsync(string key, Func<Task<string?>> find, CancellationToken ct)
    {
        // A miss is cached as "" for a day: the show may be added to the provider later.
        var value = await cache.GetOrCreateAsync(key, async _ => await find() ?? "",
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);
        if (value.Length == 0)
        {
            await cache.SetAsync(key, "", new HybridCacheEntryOptions { Expiration = MissCacheFor, LocalCacheExpiration = MissCacheFor }, cancellationToken: ct);
            return null;
        }
        return value;
    }

    /// <summary>Lower case, "&amp;" as "and", punctuation dropped, a trailing "(2019)" or "(US)" kept for the caller to strip.</summary>
    internal static string Normalise(string title) =>
        string.Join(' ', NotWord().Replace(title.Replace("&", " and ").ToLowerInvariant(), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"\s*\(?\b(19|20)\d{2}\)?\s*$|\s*\b(us|uk|au|ca)\b\s*$")]
    private static partial Regex TrailingParenthesis();

    [GeneratedRegex(@"[^\p{L}\p{Nd}()]+")]
    private static partial Regex NotWord();
}
