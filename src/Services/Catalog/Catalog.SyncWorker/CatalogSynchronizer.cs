using Catalog.Data;
using Catalog.SyncWorker.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Catalog.SyncWorker;

/// <summary>
/// Brings catalogdb in line with the provider and publishes an event for every change it finds.
/// Events are written to the outbox in the same SaveChanges as the data, then published.
/// </summary>
public sealed class CatalogSynchronizer(
    CatalogContext db,
    ICatalogProvider provider,
    IIntegrationEventOutbox outbox,
    HybridCache cache,
    TimeProvider time,
    ILogger<CatalogSynchronizer> logger)
{
    private const int RefreshBatchSize = 50;

    /// <summary>Imports featured shows that aren't in the catalog yet.</summary>
    public async Task<int> ImportFeaturedAsync(CancellationToken ct)
    {
        var externalIds = await provider.GetFeaturedShowIdsAsync(ct);
        return await ImportMissingAsync(externalIds, ct);
    }

    /// <summary>
    /// Imports the provider's best matches for a title search that aren't in the catalog yet,
    /// so Catalog.API's search can find them. Returns how many shows were imported.
    /// </summary>
    public async Task<int> ImportSearchResultsAsync(string query, int limit, CancellationToken ct)
    {
        var externalIds = await provider.SearchShowIdsAsync(query, limit, ct);
        return await ImportMissingAsync(externalIds, ct);
    }

    private async Task<int> ImportMissingAsync(IReadOnlyList<string> externalIds, CancellationToken ct)
    {
        var ids = externalIds.ToList();
        var existing = await db.Shows
            .Where(s => s.Source == provider.Name && ids.Contains(s.ExternalId))
            .Select(s => s.ExternalId)
            .ToListAsync(ct);

        var imported = 0;
        foreach (var externalId in ids.Except(existing))
        {
            var show = await provider.GetShowAsync(externalId, ct);
            if (show is null)
            {
                continue;
            }

            try
            {
                await SyncShowAsync(show, ct);
                imported++;
            }
            catch (DbUpdateException ex)
            {
                if (!await ShowExistsAsync(externalId, ct))
                {
                    throw;
                }

                // A concurrent import (the hourly sync or another search) added it first; nothing to do.
                logger.LogDebug(ex, "Show {ExternalId} was imported concurrently", externalId);
            }
        }

        return imported;
    }

    private async Task<bool> ShowExistsAsync(string externalId, CancellationToken ct)
    {
        // The failed SaveChanges left the show's entities tracked; drop them before querying again.
        db.ChangeTracker.Clear();
        return await db.Shows.AnyAsync(s => s.Source == provider.Name && s.ExternalId == externalId, ct);
    }

    /// <summary>Refreshes shows whose refresh time has come (airing shows daily, ended shows weekly, followed shows promptly).</summary>
    public async Task<int> RefreshDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        var due = await db.Shows
            .Where(s => s.Source == provider.Name && s.NextRefreshAtUtc <= now)
            .OrderBy(s => s.NextRefreshAtUtc)
            .Select(s => new { s.Id, s.ExternalId })
            .Take(RefreshBatchSize)
            .ToListAsync(ct);

        foreach (var item in due)
        {
            var providerShow = await provider.GetShowAsync(item.ExternalId, ct);
            if (providerShow is null)
            {
                logger.LogWarning("Provider no longer has show {ExternalId}; skipping for a week", item.ExternalId);
                await db.Shows.Where(s => s.Id == item.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRefreshAtUtc, now.AddDays(7)), ct);
                continue;
            }

            await SyncShowAsync(providerShow, ct);
        }

        return due.Count;
    }

    /// <summary>
    /// Resolves "similar shows" that refer to shows imported after the show itself,
    /// and publishes ShowUpdated when the list grows.
    /// </summary>
    public async Task<int> ResolveSimilarShowsAsync(CancellationToken ct)
    {
        var candidates = await db.Shows
            .Include(s => s.Cast).ThenInclude(c => c.Person)
            .Where(s => s.SimilarShowIds.Count < s.SimilarExternalIds.Count)
            .ToListAsync(ct);

        var updated = 0;
        foreach (var show in candidates)
        {
            var resolved = await ResolveSimilarAsync(show.SimilarExternalIds, ct);
            if (resolved.Count == show.SimilarShowIds.Count)
            {
                continue;
            }

            show.SimilarShowIds = resolved;
            outbox.Add(ShowUpdated(show));
            updated++;
        }

        if (updated > 0)
        {
            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
            foreach (var show in candidates)
            {
                await cache.RemoveByTagAsync(CacheKeys.ShowTag(show.Id), ct);
            }
        }

        return updated;
    }

    /// <summary>Publishes EpisodeAired for episodes whose air time has passed.</summary>
    public async Task<int> PublishAiredEpisodesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        var aired = await db.Episodes
            .Where(e => !e.AiredPublished && e.AirsAtUtc != null && e.AirsAtUtc <= now)
            .OrderBy(e => e.AirsAtUtc)
            .Take(200)
            .Join(db.Shows, e => e.ShowId, s => s.Id, (e, s) => new { Episode = e, ShowTitle = s.Title })
            .ToListAsync(ct);

        foreach (var item in aired)
        {
            var e = item.Episode;
            e.AiredPublished = true;
            outbox.Add(new EpisodeAiredIntegrationEvent(e.ShowId, item.ShowTitle, e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title));
        }

        if (aired.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
            foreach (var showId in aired.Select(a => a.Episode.ShowId).Distinct())
            {
                await cache.RemoveByTagAsync(CacheKeys.ShowTag(showId), ct);
            }
        }

        return aired.Count;
    }

    /// <summary>
    /// Republishes every show (as ShowAdded, with its current details and cast) and its episodes (EpisodesScheduled),
    /// so a service added after shows were imported can build its copy. Consumers upsert, so others just see the
    /// same data again. SeasonAnnounced isn't replayed: Notifications would re-send "new season" alerts.
    /// </summary>
    public async Task<int> ReplayAllAsync(CancellationToken ct)
    {
        const int batchSize = 20;
        var ids = await db.Shows.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(ct);

        foreach (var batch in ids.Chunk(batchSize))
        {
            var shows = await db.Shows
                .Include(s => s.Episodes)
                .Include(s => s.Cast).ThenInclude(c => c.Person)
                .AsSplitQuery()
                .Where(s => batch.Contains(s.Id))
                .ToListAsync(ct);

            foreach (var show in shows)
            {
                outbox.Add(ShowAdded(show));
                foreach (var episodes in show.Episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).Chunk(EpisodesScheduledIntegrationEvent.MaxEpisodes))
                {
                    outbox.Add(EpisodesScheduled(show, episodes));
                }
            }

            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
            db.ChangeTracker.Clear();
        }

        logger.LogInformation("Replayed {Count} shows", ids.Count);
        return ids.Count;
    }

    /// <summary>Moves a show to the front of the refresh queue (used when someone follows it).</summary>
    public Task MarkForRefreshAsync(Guid showId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return db.Shows
            .Where(s => s.Id == showId && s.NextRefreshAtUtc > now)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRefreshAtUtc, now), ct);
    }

    private async Task SyncShowAsync(ProviderShow p, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var nowUtc = now.UtcDateTime;

        var show = await db.Shows
            .Include(s => s.Seasons)
            .Include(s => s.Episodes)
            .Include(s => s.Cast).ThenInclude(c => c.Person)
            .Include(s => s.WatchProviders)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Source == provider.Name && s.ExternalId == p.ExternalId, ct);

        var isNew = show is null;
        if (show is null)
        {
            show = new Show
            {
                Id = Guid.CreateVersion7(),
                Source = provider.Name,
                ExternalId = p.ExternalId,
                Title = p.Title,
                Status = p.Status,
                CreatedAtUtc = nowUtc
            };
            db.Shows.Add(show);
        }

        var oldStatus = show.Status;
        var events = new List<IntegrationEvent>();
        var scheduled = new List<Episode>();

        // Details
        var detailsChanged = isNew
            || show.Title != p.Title
            || show.Overview != p.Overview
            || show.PosterUrl != p.PosterUrl
            || show.BackdropUrl != p.BackdropUrl
            || show.Network != p.Network
            || show.FirstAirDate != p.FirstAirDate
            || !show.Genres.SequenceEqual(p.Genres)
            || !show.Keywords.SequenceEqual(p.Keywords)
            || !show.SimilarExternalIds.SequenceEqual(p.SimilarExternalIds)
            || show.ExternalRating != p.Rating
            || Math.Abs(show.Popularity - p.Popularity) >= 5;

        show.Title = p.Title;
        show.Overview = p.Overview;
        show.PosterUrl = p.PosterUrl;
        show.BackdropUrl = p.BackdropUrl;
        show.Network = p.Network;
        show.FirstAirDate = p.FirstAirDate;
        show.Genres = p.Genres.ToList();
        show.Keywords = p.Keywords.ToList();
        show.Popularity = p.Popularity;
        show.ExternalRating = p.Rating;
        show.SimilarExternalIds = p.SimilarExternalIds.ToList();
        show.SimilarShowIds = await ResolveSimilarAsync(p.SimilarExternalIds, ct);
        show.Status = p.Status;

        detailsChanged |= await SyncCastAsync(show, p.Cast, ct);
        SyncWatchProviders(show, p.WatchOptions);

        // Seasons
        foreach (var ps in p.Seasons)
        {
            var season = show.Seasons.FirstOrDefault(s => s.Number == ps.Number);
            if (season is null)
            {
                show.Seasons.Add(new Season
                {
                    Id = Guid.CreateVersion7(),
                    ShowId = show.Id,
                    Number = ps.Number,
                    Name = ps.Name,
                    Overview = ps.Overview,
                    PremieresAtUtc = ps.PremieresAtUtc
                });

                if (!isNew)
                {
                    events.Add(new SeasonAnnouncedIntegrationEvent(show.Id, show.Title, ps.Number, ps.PremieresAtUtc));
                }
            }
            else
            {
                season.Name = ps.Name;
                season.Overview = ps.Overview;
                season.PremieresAtUtc = ps.PremieresAtUtc;
            }
        }

        // Episodes
        var existingEpisodes = show.Episodes.ToDictionary(e => e.ExternalId);
        foreach (var pe in p.Episodes)
        {
            if (!existingEpisodes.TryGetValue(pe.ExternalId, out var episode))
            {
                episode = new Episode
                {
                    Id = Guid.CreateVersion7(),
                    ShowId = show.Id,
                    ExternalId = pe.ExternalId,
                    SeasonNumber = pe.SeasonNumber,
                    EpisodeNumber = pe.EpisodeNumber,
                    Title = pe.Title,
                    Overview = pe.Overview,
                    AirsAtUtc = pe.AirsAtUtc,
                    RuntimeMinutes = pe.RuntimeMinutes,
                    ExternalRating = pe.Rating,
                    // Episodes that had already aired when imported never trigger "new episode" alerts.
                    AiredPublished = pe.AirsAtUtc <= now
                };
                show.Episodes.Add(episode);
                scheduled.Add(episode);
                continue;
            }

            var changed = episode.Title != pe.Title
                || episode.Overview != pe.Overview
                || episode.AirsAtUtc != pe.AirsAtUtc
                || episode.RuntimeMinutes != pe.RuntimeMinutes
                || episode.ExternalRating != pe.Rating
                || episode.SeasonNumber != pe.SeasonNumber
                || episode.EpisodeNumber != pe.EpisodeNumber;

            if (!changed)
            {
                continue;
            }

            if (episode.AirsAtUtc != pe.AirsAtUtc && pe.AirsAtUtc > now)
            {
                // Rescheduled into the future: alert again when it airs.
                episode.AiredPublished = false;
            }

            episode.Title = pe.Title;
            episode.Overview = pe.Overview;
            episode.AirsAtUtc = pe.AirsAtUtc;
            episode.RuntimeMinutes = pe.RuntimeMinutes;
            episode.ExternalRating = pe.Rating;
            episode.SeasonNumber = pe.SeasonNumber;
            episode.EpisodeNumber = pe.EpisodeNumber;
            scheduled.Add(episode);
        }

        // Show-level events go first so consumers know the show before its episodes.
        var showEvents = new List<IntegrationEvent>();
        if (isNew)
        {
            showEvents.Add(ShowAdded(show));
        }
        else
        {
            if (detailsChanged)
            {
                showEvents.Add(ShowUpdated(show));
            }
            if (oldStatus != show.Status)
            {
                showEvents.Add(new ShowStatusChangedIntegrationEvent(show.Id, show.Title, oldStatus, show.Status));
            }
        }

        events.AddRange(scheduled.Chunk(EpisodesScheduledIntegrationEvent.MaxEpisodes).Select(batch => EpisodesScheduled(show, batch)));

        foreach (var e in showEvents.Concat(events))
        {
            outbox.Add(e);
        }

        show.LastSyncedAtUtc = nowUtc;
        show.NextRefreshAtUtc = show.Status is ShowStatus.Ended or ShowStatus.Cancelled
            ? nowUtc.AddDays(7)
            : nowUtc.AddDays(1);

        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);

        await cache.RemoveByTagAsync(CacheKeys.ShowTag(show.Id), ct);
        if (isNew || detailsChanged)
        {
            await cache.RemoveAsync(CacheKeys.Trending, ct);
        }

        logger.LogInformation("Synced {Title} ({ExternalId}): new={IsNew}, events={EventCount}",
            show.Title, show.ExternalId, isNew, showEvents.Count + events.Count);
    }

    /// <summary>Updates the cast in place. Returns true if it changed.</summary>
    private async Task<bool> SyncCastAsync(Show show, IReadOnlyList<ProviderCastMember> cast, CancellationToken ct)
    {
        var changed = false;
        var wanted = cast.Select((c, i) => (Member: c, Order: i)).ToDictionary(x => x.Member.Name, x => x);

        foreach (var existing in show.Cast.ToList())
        {
            if (!wanted.ContainsKey(existing.Person.Name))
            {
                show.Cast.Remove(existing);
                changed = true;
            }
        }

        foreach (var (name, (member, order)) in wanted)
        {
            var existing = show.Cast.FirstOrDefault(c => c.Person.Name == name);

            // People stored before the provider's id was kept were matched by name, so one row may hold two actors
            // who share a name. When the stored person isn't this provider id, resolve the right one; if that's a
            // different row, re-point the credit to it.
            var person = existing is not null && (member.Person is null || existing.Person.ExternalId == member.Person.ExternalId)
                ? existing.Person
                : await GetOrCreatePersonAsync(member, ct);
            if (existing is not null && existing.PersonId != person.Id)
            {
                show.Cast.Remove(existing);
                existing = null;
            }

            if (existing is null)
            {
                show.Cast.Add(new CastMember
                {
                    ShowId = show.Id,
                    PersonId = person.Id,
                    Person = person,
                    Character = member.Character,
                    CharacterExternalIds = member.CharacterExternalIds.ToList(),
                    Order = order
                });
                changed = true;
                continue;
            }

            // Existing cast too, so details added later (or corrected) reach people already in the catalog.
            UpdatePerson(person, member);

            if (existing.Character != member.Character
                || existing.Order != order
                || !existing.CharacterExternalIds.SequenceEqual(member.CharacterExternalIds))
            {
                existing.Character = member.Character;
                existing.CharacterExternalIds = member.CharacterExternalIds.ToList();
                existing.Order = order;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Finds the person by the provider's id; else by name among people without one (adopting it), so earlier rows
    /// keyed by name gain the id; else creates them. Without a provider id, matches by name only.
    /// </summary>
    private async Task<Person> GetOrCreatePersonAsync(ProviderCastMember member, CancellationToken ct)
    {
        Person? person;
        if (member.Person?.ExternalId is { } externalId)
        {
            person = db.People.Local.FirstOrDefault(p => p.ExternalId == externalId)
                ?? await db.People.FirstOrDefaultAsync(p => p.ExternalId == externalId, ct)
                ?? db.People.Local.FirstOrDefault(p => p.Name == member.Name && p.ExternalId == null)
                ?? await db.People.FirstOrDefaultAsync(p => p.Name == member.Name && p.ExternalId == null, ct);
        }
        else
        {
            person = db.People.Local.FirstOrDefault(p => p.Name == member.Name)
                ?? await db.People.FirstOrDefaultAsync(p => p.Name == member.Name, ct);
        }

        if (person is null)
        {
            person = new Person { Id = Guid.CreateVersion7(), Name = member.Name };
            db.People.Add(person);
        }

        UpdatePerson(person, member);
        return person;
    }

    private static void UpdatePerson(Person person, ProviderCastMember member)
    {
        if (member.PhotoUrl is not null)
        {
            person.PhotoUrl = member.PhotoUrl;
        }

        if (member.Person is { } details)
        {
            person.ExternalId = details.ExternalId;
            person.Birthday = details.Birthday;
            person.Deathday = details.Deathday;
            person.Gender = details.Gender;
            person.Country = details.Country;
        }
    }

    private static void SyncWatchProviders(Show show, IReadOnlyList<ProviderWatchOption> options)
    {
        show.WatchProviders.Clear();
        foreach (var o in options)
        {
            show.WatchProviders.Add(new WatchProvider
            {
                Id = Guid.CreateVersion7(),
                ShowId = show.Id,
                Region = o.Region,
                ProviderName = o.ProviderName,
                Type = o.Type,
                Url = o.Url
            });
        }
    }

    private async Task<List<Guid>> ResolveSimilarAsync(IReadOnlyList<string> externalIds, CancellationToken ct)
    {
        if (externalIds.Count == 0)
        {
            return [];
        }

        var ids = externalIds.ToList();
        var found = await db.Shows
            .Where(s => s.Source == provider.Name && ids.Contains(s.ExternalId))
            .Select(s => new { s.Id, s.ExternalId })
            .ToListAsync(ct);

        // Keep the provider's order (most similar first).
        return ids
            .Select(id => found.FirstOrDefault(f => f.ExternalId == id)?.Id)
            .OfType<Guid>()
            .ToList();
    }

    /// <summary>ShowAdded from stored data (after the cast is synced, so credits carry person ids); also used for a replay.</summary>
    private static ShowAddedIntegrationEvent ShowAdded(Show s)
    {
        var cast = s.Cast.OrderBy(c => c.Order).ToList();
        return new(s.Id, s.Title, s.PosterUrl, s.Status, s.Genres, s.Keywords, cast.Select(c => c.Person.Name).ToList(), s.SimilarShowIds, s.Popularity,
            s.ExternalRating, cast.Select(c => new CastCredit(c.Person.Name, c.Character, c.CharacterExternalIds, c.PersonId)).ToList());
    }

    private static ShowUpdatedIntegrationEvent ShowUpdated(Show s)
    {
        var cast = s.Cast.OrderBy(c => c.Order).ToList();
        return new(s.Id, s.Title, s.PosterUrl, s.Status, s.Genres, s.Keywords, cast.Select(c => c.Person.Name).ToList(), s.SimilarShowIds, s.Popularity,
            s.ExternalRating, cast.Select(c => new CastCredit(c.Person.Name, c.Character, c.CharacterExternalIds, c.PersonId)).ToList());
    }

    private static EpisodesScheduledIntegrationEvent EpisodesScheduled(Show s, IEnumerable<Episode> episodes) =>
        new(s.Id, s.Title, episodes
            .Select(e => new ScheduledEpisode(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirsAtUtc, e.RuntimeMinutes, e.ExternalRating))
            .ToList());
}
