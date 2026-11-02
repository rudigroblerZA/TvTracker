using Catalog.Data;
using Catalog.SyncWorker.Providers;
using Microsoft.EntityFrameworkCore;

namespace Catalog.SyncWorker;

/// <summary>
/// Refreshes what the provider changed instead of everything on a timer: each hour, before the refresh, the provider's
/// update lists (one call each for shows and people) mark the catalog's changed shows due now, and its changed people's
/// cast credits and careers stale. The fixed schedules stay as a slower safety net (<see cref="CatalogSynchronizer.NextRefresh"/>,
/// 120 days for people).
/// <list type="bullet">
/// <item><b>TVmaze's edge cache:</b> its answers are cached for up to an hour, so a show fetched within an hour of a change
/// may come back as it was. A change counts when it's older than <see cref="CacheLag"/> (newer ones wait for the next
/// poll) and newer than the last sync less <see cref="CacheLag"/> (so a sync that read a cached copy is repeated once).</item>
/// <item><b>First look:</b> after a start, the past <see cref="FirstLook"/>, which covers a worker that was down; then the
/// time since the last good poll plus <see cref="CacheLag"/>.</item>
/// </list>
/// </summary>
public sealed class ProviderChanges(IServiceScopeFactory scopes, TimeProvider time, ILogger<ProviderChanges> logger)
{
    public static readonly TimeSpan CacheLag = TimeSpan.FromHours(1);
    public static readonly TimeSpan FirstLook = TimeSpan.FromDays(7);

    /// <summary>
    /// What a person marked stale is stamped with: older than any refresh, so due at once and before people merely due.
    /// </summary>
    public static readonly DateTime Stale = DateTime.UnixEpoch;

    private DateTimeOffset? lastPolled;

    /// <returns>Shows marked due and people marked stale; nulls when the provider keeps no update list.</returns>
    public async Task<(int? Shows, int? People)> MarkAsync(CancellationToken ct)
    {
        using var caller = TvMazeCaller.Begin("updates", TvMazeLane.Background);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var provider = scope.ServiceProvider.GetRequiredService<ICatalogProvider>();
        var now = time.GetUtcNow();
        var within = lastPolled is { } last ? now - last + CacheLag : FirstLook;

        int? shows = null, people = null;
        if (await provider.GetChangedShowsAsync(within, ct) is { } changedShows)
        {
            var keys = changedShows.Keys.ToList();
            var known = await db.Shows.AsNoTracking()
                .Where(s => s.Source == provider.Name && keys.Contains(s.ExternalId))
                .Select(s => new { s.Id, s.ExternalId, s.LastSyncedAtUtc })
                .ToListAsync(ct);
            var due = Changed(changedShows, known.Select(s => (s.Id, s.ExternalId, (DateTime?)s.LastSyncedAtUtc)), now);
            var nowUtc = now.UtcDateTime;
            shows = due.Count == 0 ? 0 : await db.Shows
                .Where(s => due.Contains(s.Id) && s.NextRefreshAtUtc > nowUtc)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRefreshAtUtc, nowUtc), ct);
        }

        if (await provider.GetChangedPeopleAsync(within, ct) is { } changedPeople)
        {
            var keys = changedPeople.Keys.ToList();
            var known = await db.People.AsNoTracking()
                .Where(p => p.ExternalId != null && keys.Contains(p.ExternalId))
                .Select(p => new { p.Id, p.ExternalId, p.CastCreditsRefreshedAtUtc, p.CareerRefreshedAtUtc })
                .ToListAsync(ct);
            var credits = Changed(changedPeople, known.Where(p => p.CastCreditsRefreshedAtUtc is not null)
                .Select(p => (p.Id, p.ExternalId!, p.CastCreditsRefreshedAtUtc)), now);
            var careers = Changed(changedPeople, known.Where(p => p.CareerRefreshedAtUtc is not null)
                .Select(p => (p.Id, p.ExternalId!, p.CareerRefreshedAtUtc)), now);
            if (credits.Count > 0)
            {
                await db.People.Where(p => credits.Contains(p.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.CastCreditsRefreshedAtUtc, Stale), ct);
            }
            if (careers.Count > 0)
            {
                await db.People.Where(p => careers.Contains(p.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.CareerRefreshedAtUtc, Stale), ct);
            }
            people = credits.Union(careers).Count();
        }

        lastPolled = now;
        logger.LogInformation("Provider changes (last {Hours:0} h): {Shows} shows due, {People} people stale", within.TotalHours, shows, people);
        return (shows, people);
    }

    /// <summary>
    /// The ids among <paramref name="known"/> the provider changed since their last fetch (see the class remarks for the
    /// cache lag), from the provider's change list.
    /// </summary>
    internal static List<Guid> Changed(IReadOnlyDictionary<string, DateTimeOffset> changes, IEnumerable<(Guid Id, string ExternalId, DateTime? FetchedAtUtc)> known,
        DateTimeOffset now) =>
        known.Where(k => changes.TryGetValue(k.ExternalId, out var at) && at <= now - CacheLag
                && (k.FetchedAtUtc is not { } fetched || at > new DateTimeOffset(fetched, TimeSpan.Zero) - CacheLag))
            .Select(k => k.Id)
            .ToList();
}
