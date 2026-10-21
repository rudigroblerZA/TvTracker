using Catalog.Data;
using Catalog.SyncWorker.Providers;
using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Catalog.SyncWorker;

/// <summary>
/// Keeps "who made it" current, a little at a time so the provider's rate limit stays shared with everything else.
/// Every minute it:
/// <list type="bullet">
/// <item>re-syncs shows without creatives (imported before they were, or whose crew the provider failed to return), and</item>
/// <item>fetches career figures for the people that matter for the "prolific producer" insight: every creator or
/// developer, and anyone credited on at least two of the catalog's shows. Figures are refreshed monthly.</item>
/// </list>
/// </summary>
public sealed class CreativesService(
    IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<CreativesService> logger) : BackgroundService
{
    private const int ShowsPerRound = 30;
    private const int PeoplePerRound = 30;
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CareerMaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan BackfillRetry = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var shows = await BackfillShowsAsync(stoppingToken);
                var people = await RefreshCareersAsync(stoppingToken);
                if (shows + people > 0)
                {
                    logger.LogInformation("Creatives: backfilled {Shows} shows, refreshed {People} careers", shows, people);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Updating creatives failed; retrying in {Round}", Round);
            }

            await Task.Delay(Round, time, stoppingToken);
        }
    }

    private async Task<int> BackfillShowsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var provider = scope.ServiceProvider.GetRequiredService<ICatalogProvider>();
        var synchronizer = scope.ServiceProvider.GetRequiredService<CatalogSynchronizer>();

        // Synced over an hour ago: a show whose crew the provider failed to return is retried hourly, not every round.
        var syncedBefore = time.GetUtcNow().UtcDateTime - BackfillRetry;
        var pending = await db.Shows
            .Where(s => !s.CreativesImported && s.Source == provider.Name && s.LastSyncedAtUtc < syncedBefore)
            .OrderByDescending(s => s.Popularity)
            .Select(s => s.ExternalId)
            .Take(ShowsPerRound)
            .ToListAsync(ct);

        foreach (var externalId in pending)
        {
            if (await provider.GetShowAsync(externalId, ct) is { } show)
            {
                await synchronizer.RefreshAsync(show, ct);
            }
        }

        return pending.Count;
    }

    private async Task<int> RefreshCareersAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var provider = scope.ServiceProvider.GetRequiredService<ICatalogProvider>();
        var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();
        var staleBefore = time.GetUtcNow().UtcDateTime - CareerMaxAge;

        var due = await db.People
            .Where(p => p.ExternalId != null && (p.CareerRefreshedAtUtc == null || p.CareerRefreshedAtUtc < staleBefore))
            .Where(p => db.Creatives.Any(c => c.PersonId == p.Id && (c.Roles.Contains("Creator") || c.Roles.Contains("Developer")))
                || db.Creatives.Count(c => c.PersonId == p.Id) >= 2)
            .OrderBy(p => p.CareerRefreshedAtUtc != null)
            .ThenByDescending(p => db.Creatives.Count(c => c.PersonId == p.Id))
            .Take(PeoplePerRound)
            .ToListAsync(ct);

        foreach (var person in due)
        {
            var career = await provider.GetCareerAsync(person.ExternalId!, ct);
            person.CareerShows = career?.Shows;
            person.CareerCreated = career?.Created;
            person.CareerExecutiveProduced = career?.ExecutiveProduced;
            person.CareerNotableShows = career?.NotableShows.ToList() ?? [];
            person.CareerRefreshedAtUtc = time.GetUtcNow().UtcDateTime;
        }

        // Analytics tags shows from prolific people with these.
        foreach (var careers in CatalogSynchronizer.CareerEvents(due))
        {
            outbox.Add(careers);
        }
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        return due.Count;
    }
}
