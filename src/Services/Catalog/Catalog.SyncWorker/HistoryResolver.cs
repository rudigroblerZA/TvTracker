using Catalog.Data;
using Catalog.SyncWorker.Namesakes;
using Catalog.SyncWorker.Providers;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalog.SyncWorker;

/// <summary>
/// Finds the provider's show for each entry of someone's history export and makes sure the catalog has it, for Bring
/// your history. A TheTVDB id is looked up directly (<see cref="ICatalogProvider.LookUpShowByTvdbAsync"/>); otherwise the
/// title is searched and taken only when a result's title matches it as Same name compares titles
/// (<see cref="NamesakeTitles.Key"/>; and its year, when given, within a year), since the first search result for "Ghosts"
/// or "The Office" is a coin toss. Lookups are cached for <see cref="CacheFor"/>
/// (misses too, for a day), so a second import of the same export costs no provider calls for lookups.
/// </summary>
public sealed class HistoryResolver(ICatalogProvider provider, CatalogSynchronizer synchronizer, HybridCache cache, ILogger<HistoryResolver> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissCacheFor = TimeSpan.FromDays(1);

    public async Task<ResolveShowsResult> ResolveAsync(ResolveShowsRequest request, CancellationToken ct)
    {
        var results = new List<ResolvedShow>();
        foreach (var show in request.Shows)
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
            var found = await CachedAsync(CacheKeys.HistoryTvdb(provider.Name, tvdb), () => provider.LookUpShowByTvdbAsync(tvdb, ct), ct);
            if (found is not null)
            {
                return found;
            }
        }
        if (show.Title is not { Length: > 1 } title)
        {
            return null;
        }
        var wanted = NamesakeTitles.Key(title);
        return await CachedAsync(CacheKeys.HistoryTitle(provider.Name, wanted, show.Year), async () =>
        {
            var results = await provider.SearchShowsAsync(title, ct);
            bool YearFits(ProviderShowSummary s) => show.Year is not { } y || s.Premiered is not { } p || Math.Abs(p.Year - y) <= 1;
            // The same title as Same name reads it (a trailing year or country ignored), then a year match alone.
            var match = results.FirstOrDefault(s => NamesakeTitles.Key(s.Title) == wanted && YearFits(s))
                ?? (show.Year is not null ? results.FirstOrDefault(s => s.Premiered?.Year == show.Year && NamesakeTitles.Key(s.Title).Contains(wanted)) : null);
            return match?.ExternalId;
        }, ct);
    }

    private async Task<string?> CachedAsync(string key, Func<Task<string?>> find, CancellationToken ct)
    {
        // A miss is cached as "" for a day only: the show may be added to the provider later.
        var looked = false;
        var value = await cache.GetOrCreateAsync(key, async _ => { looked = true; return await find() ?? ""; },
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);
        if (looked && value.Length == 0)
        {
            await cache.SetAsync(key, "", new HybridCacheEntryOptions { Expiration = MissCacheFor, LocalCacheExpiration = MissCacheFor }, cancellationToken: ct);
        }
        return value.Length == 0 ? null : value;
    }
}
