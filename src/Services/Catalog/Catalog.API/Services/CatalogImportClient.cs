using System.Net.Http.Json;
using Catalog.Data;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalog.API.Services;

/// <summary>
/// Asks Catalog.SyncWorker to import the provider's best matches for a search term, so search finds shows
/// the catalog hasn't imported yet. Each term is sent at most once per <see cref="ImportInterval"/>; concurrent
/// searches for the same term share one call, and <see cref="SearchImportLimiter"/> caps new terms across all callers.
/// Failures are logged and search carries on with what the catalog has.
/// </summary>
public sealed class CatalogImportClient(HttpClient http, HybridCache cache, SearchImportLimiter limiter, ILogger<CatalogImportClient> logger)
{
    private static readonly TimeSpan ImportInterval = TimeSpan.FromHours(6);

    /// <summary>Longest a search waits for imports; the matches still land in the catalog for the next search.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

    public async Task ImportSearchMatchesAsync(string search, CancellationToken ct)
    {
        var query = string.Join(' ', search.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (query.Length < 2)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MaxWait);

        try
        {
            await cache.GetOrCreateAsync(
                CacheKeys.SearchImport(query),
                async token =>
                {
                    if (!limiter.TryAcquire())
                    {
                        // Thrown so nothing is cached: the term is tried again on a later search.
                        throw new ImportCapReachedException();
                    }

                    using var response = await http.PostAsJsonAsync("api/v1/imports/search", new { Query = query }, token);
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<SearchImportResult>(token);
                    return result?.Imported ?? 0;
                },
                new HybridCacheEntryOptions { Expiration = ImportInterval, LocalCacheExpiration = ImportInterval },
                cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Importing search matches for {Query} took longer than {MaxWait}; searching the catalog as it is", query, MaxWait);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Importing search matches for {Query} failed; searching the catalog as it is", query);
        }
        catch (ImportCapReachedException)
        {
            logger.LogInformation("Search import cap reached; searching the catalog as it is for {Query}", query);
        }
    }

    /// <summary>
    /// Bring your history: asks the worker to find (and import when missing) up to <see cref="HistoryImports.MaxShows"/> shows
    /// of an export, by TheTVDB id or title. Not cached here: the worker caches each lookup, and the shows are then in the
    /// catalog. One call is up to ~25 provider calls, so it can take a dozen seconds.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedShow>> ResolveAsync(IReadOnlyList<ResolveShowRequest> shows, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/v1/imports/resolve", new ResolveShowsRequest(shows), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ResolveShowsResult>(ct))?.Shows ?? [];
    }

    private sealed class ImportCapReachedException : Exception;

    private sealed record SearchImportResult(int Imported);
}

