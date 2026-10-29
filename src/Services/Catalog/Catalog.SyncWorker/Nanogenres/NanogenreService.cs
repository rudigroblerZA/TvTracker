using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Catalog.SyncWorker.Nanogenres;

/// <summary>
/// Rediscovers the catalog's nanogenres (see <see cref="NanogenreDiscovery"/>) every <see cref="Interval"/>, a few
/// minutes after starting so new embeddings are in. It replaces the nanogenres table, updates each show's list, and
/// tells other services (Recommendations) which shows changed; every show on the first run after starting and once a
/// day after, so lost events heal (RabbitMQ has no dead-letter queue).
/// </summary>
public sealed class NanogenreService(
    IServiceScopeFactory scopeFactory, EmbeddingModel model, TimeProvider time, ILogger<NanogenreService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FullSendInterval = TimeSpan.FromDays(1);

    /// <summary>While fewer embedded shows than this, discovery waits this long and tries again.</summary>
    private static readonly TimeSpan NotReadyRetry = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Discovery runs only once this share of shows has an embedding from the current model. Earlier (a fresh catalog,
    /// or just after changing the model) it would find almost no clusters and replace every nanogenre with nothing.
    /// </summary>
    private const double MinEmbeddedShare = 0.8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, time, stoppingToken);
        DateTimeOffset? lastFullSend = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = Interval;
            try
            {
                var publishAll = lastFullSend is null || time.GetUtcNow() - lastFullSend >= FullSendInterval;
                if (await RefreshAsync(publishAll, stoppingToken))
                {
                    lastFullSend = publishAll ? time.GetUtcNow() : lastFullSend;
                }
                else
                {
                    delay = NotReadyRetry;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Discovering nanogenres failed; retrying in {Interval}", Interval);
            }

            await Task.Delay(delay, time, stoppingToken);
        }
    }

    /// <returns>False when too few shows are embedded yet to run.</returns>
    private async Task<bool> RefreshAsync(bool publishAll, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();

        var shows = await db.Shows.AsNoTracking().Select(s => new { s.Id, s.Genres, s.Keywords, s.Nanogenres }).ToListAsync(ct);
        var embeddings = await db.ShowEmbeddings.AsNoTracking()
            .Where(e => e.Model == model.Id)
            .ToDictionaryAsync(e => e.ShowId, e => e.Embedding.ToArray(), ct);
        if (embeddings.Count < shows.Count * MinEmbeddedShare)
        {
            logger.LogInformation("Nanogenres: only {Embedded} of {Shows} shows embedded with {Model}; keeping the current ones for now",
                embeddings.Count, shows.Count, model.Id);
            return false;
        }

        var found = NanogenreDiscovery.Discover(
            shows.Select(s => new NanogenreShow(s.Id, s.Genres, s.Keywords, embeddings.GetValueOrDefault(s.Id))).ToList(),
            model.MaxDistance);

        // Each show's nanogenres, most specific (smallest) first.
        var byShow = found
            .OrderBy(n => n.ShowIds.Count).ThenBy(n => n.Name, StringComparer.Ordinal)
            .SelectMany(n => n.ShowIds.Select(id => (ShowId: id, Nanogenre: n)))
            .ToLookup(x => x.ShowId, x => x.Nanogenre);

        var computedAt = time.GetUtcNow().UtcDateTime;
        var changed = new List<ShowNanogenreNames>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            changed.Clear();
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Nanogenres.ExecuteDeleteAsync(ct);
            db.Nanogenres.AddRange(found.Select(n => new Nanogenre
            {
                Slug = n.Slug,
                Name = n.Name,
                Source = n.Source,
                ShowCount = n.ShowIds.Count,
                ComputedAtUtc = computedAt
            }));

            var updates = new List<(Guid ShowId, List<string> Slugs)>();
            foreach (var show in shows)
            {
                var mine = byShow[show.Id].ToList();
                var slugs = mine.Select(n => n.Slug).ToList();
                var differs = !slugs.SequenceEqual(show.Nanogenres);
                if (differs)
                {
                    updates.Add((show.Id, slugs));
                }
                if (publishAll || differs)
                {
                    changed.Add(new ShowNanogenreNames(show.Id, mine.Select(n => n.Name).ToList()));
                }
            }

            // One update per distinct list rather than per show: after a model change most shows change, and many
            // share a list (most often none), so the transaction holds the shows' rows for far fewer round trips.
            foreach (var group in updates.GroupBy(u => string.Join('\n', u.Slugs)))
            {
                var ids = group.Select(u => u.ShowId).ToList();
                var slugs = group.First().Slugs;
                await db.Shows.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.Nanogenres, slugs), ct);
            }

            foreach (var chunk in changed.Chunk(ShowNanogenresChangedIntegrationEvent.MaxShows))
            {
                outbox.Add(new ShowNanogenresChangedIntegrationEvent(chunk));
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
        await outbox.PublishAddedAsync(ct);

        logger.LogInformation("Nanogenres: {Count} found ({Clusters} clusters, {Pairings} genre pairings), {Shows} shows in at least one, {Changed} shows sent",
            found.Count, found.Count(n => n.Source == "cluster"), found.Count(n => n.Source == "genres"),
            shows.Count(s => byShow[s.Id].Any()), changed.Count);
        return true;
    }
}
