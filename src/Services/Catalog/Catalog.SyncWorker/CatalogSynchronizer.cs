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

    /// <summary>A show the provider calls airing, with nothing aired for this long and nothing coming, is presumed ended.</summary>
    private const int PresumedEndedAfterYears = 4;

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
            try
            {
                if ((await ImportNewAsync(externalId, ct)).Outcome == ImportOutcome.Imported)
                {
                    imported++;
                }
            }
            catch (HttpRequestException ex)
            {
                // The provider failing one show (it happens per show in an outage) shouldn't stop the rest importing.
                logger.LogWarning(ex, "Couldn't import show {ExternalId}; skipping it", externalId);
            }
        }

        return imported;
    }

    /// <summary>Applies the provider's current details of a show already fetched (e.g. to backfill new fields).</summary>
    public Task RefreshAsync(ProviderShow show, CancellationToken ct) => SyncShowAsync(show, ct);

    /// <summary>Imports one show by the provider's id, unless the catalog already has it.</summary>
    public async Task<ImportResult> ImportShowAsync(string externalId, CancellationToken ct) =>
        await db.Shows.Where(s => s.Source == provider.Name && s.ExternalId == externalId).Select(s => s.Title).FirstOrDefaultAsync(ct) is { } title
            ? new ImportResult(ImportOutcome.AlreadyInCatalog, title)
            : await ImportNewAsync(externalId, ct);

    private async Task<ImportResult> ImportNewAsync(string externalId, CancellationToken ct)
    {
        var show = await provider.GetShowAsync(externalId, ct);
        if (show is null)
        {
            return new ImportResult(ImportOutcome.NotFound, null);
        }

        try
        {
            await SyncShowAsync(show, ct);
            return new ImportResult(ImportOutcome.Imported, show.Title);
        }
        catch (DbUpdateException ex)
        {
            if (!await ShowExistsAsync(externalId, ct))
            {
                throw;
            }

            // A concurrent import (the hourly sync or another search) added it first; nothing to do.
            logger.LogDebug(ex, "Show {ExternalId} was imported concurrently", externalId);
            return new ImportResult(ImportOutcome.AlreadyInCatalog, show.Title);
        }
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
            ProviderShow? providerShow;
            try
            {
                providerShow = await provider.GetShowAsync(item.ExternalId, ct);
            }
            catch (HttpRequestException ex)
            {
                // One show the provider is failing shouldn't hold up the rest of the queue; try it again in an hour.
                logger.LogWarning(ex, "Couldn't refresh show {ExternalId}; retrying in an hour", item.ExternalId);
                await db.Shows.Where(s => s.Id == item.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRefreshAtUtc, now.AddHours(1)), ct);
                continue;
            }

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

    /// <summary>
    /// The provider's status, except that a show it still calls airing with no episode for
    /// <see cref="PresumedEndedAfterYears"/> years and nothing scheduled is presumed ended. TVmaze leaves some finished
    /// shows "Running" for years (Girls, last aired 2017; Peaky Blinders, 2022), often with an undated, empty next
    /// season, which therefore doesn't count as coming. A scheduled episode, a TBA one, or a dated season brings the
    /// show back to airing on its next refresh.
    /// </summary>
    private ShowStatus StatusOf(ProviderShow p)
    {
        var now = time.GetUtcNow();
        if (p.Status != ShowStatus.Airing
            || p.Episodes.Any(e => e.AirsAtUtc is null || e.AirsAtUtc > now)
            || p.Seasons.Any(s => s.PremieresAtUtc > now))
        {
            return p.Status;
        }

        var last = p.Episodes.Max(e => e.AirsAtUtc);
        return last < now.AddYears(-PresumedEndedAfterYears) ? ShowStatus.Ended : p.Status;
    }

    /// <summary>
    /// Stores an accepted correction and applies it now, publishing the episode's change like a refresh would.
    /// Returns false when the show or episode isn't in the catalog.
    /// </summary>
    public async Task<bool> AddOverrideAsync(CatalogOverride o, CancellationToken ct)
    {
        var show = await db.Shows.Include(s => s.Episodes).SingleOrDefaultAsync(s => s.Id == o.ShowId, ct);
        if (show is null || o.EpisodeId is { } episodeId && show.Episodes.All(e => e.Id != episodeId))
        {
            return false;
        }

        // Saved research re-sends its fields under the same ids, so an existing override takes the new value (and
        // counts as the latest, since later overrides of a field win).
        o.CreatedAtUtc = time.GetUtcNow().UtcDateTime;
        if (await db.Overrides.FindAsync([o.Id], ct) is { } existing)
        {
            existing.Value = o.Value;
            existing.CreatedAtUtc = o.CreatedAtUtc;
            o = existing;
        }
        else
        {
            db.Overrides.Add(o);
        }
        // Show fields (summary, network, premiere) live only in the catalog; no other service copies them.
        if (CatalogOverrides.ApplyTo(show, o) && o.EpisodeId is { } changed)
        {
            outbox.Add(EpisodesScheduled(show, show.Episodes.Where(e => e.Id == changed)));
        }
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        await cache.RemoveByTagAsync(CacheKeys.ShowTag(show.Id), ct);
        logger.LogInformation("Override {Id} applied to {Show}: {Field}", o.Id, show.Title, o.Field);
        return true;
    }

    /// <summary>Removes a correction; the show's next refresh (due now) brings the provider's value back.</summary>
    public async Task<bool> RemoveOverrideAsync(Guid id, CancellationToken ct)
    {
        if (await db.Overrides.FindAsync([id], ct) is not { } o)
        {
            return false;
        }
        db.Overrides.Remove(o);
        await db.Shows.Where(s => s.Id == o.ShowId).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRefreshAtUtc, time.GetUtcNow().UtcDateTime), ct);
        await db.SaveChangesAsync(ct);
        return true;
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
                .Include(s => s.Creatives).ThenInclude(c => c.Person)
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

        // Careers last, so consumers know the shows' creatives first (they're independent rows anyway).
        foreach (var careers in CareerEvents(await db.People.AsNoTracking().Where(p => p.CareerShows != null).ToListAsync(ct)))
        {
            outbox.Add(careers);
        }
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);

        // Actors' roles across the provider (the reunion radar), in batches: there are thousands of rows.
        var actors = await db.People.AsNoTracking().Where(p => p.CastCreditsRefreshedAtUtc != null).ToListAsync(ct);
        foreach (var batch in actors.Chunk(PeopleCastCreditsUpdatedIntegrationEvent.MaxPeople))
        {
            var personIds = batch.Select(p => p.Id).ToList();
            var credits = (await db.CastCredits.AsNoTracking().Where(c => personIds.Contains(c.PersonId)).ToListAsync(ct)).ToLookup(c => c.PersonId);
            outbox.Add(CastCreditsEvent(batch.Select(p => (p, credits[p.Id]))));
            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
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
            .Include(s => s.Creatives).ThenInclude(c => c.Person)
            .Include(s => s.WatchProviders)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Source == provider.Name && s.ExternalId == p.ExternalId, ct);

        var isNew = show is null;
        if (show is not null && await db.Overrides.AsNoTracking().Where(o => o.ShowId == show.Id).ToListAsync(ct) is { Count: > 0 } overrides)
        {
            p = CatalogOverrides.Apply(p, overrides, show.Episodes.ToDictionary(e => e.Id, e => e.ExternalId));
        }
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
            || (!p.ImagesMissing && (show.BackdropUrl != p.BackdropUrl || show.BannerUrl != p.BannerUrl))
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
        if (!p.ImagesMissing)
        {
            show.BackdropUrl = p.BackdropUrl;
            show.BannerUrl = p.BannerUrl;
        }
        show.Network = p.Network;
        show.FirstAirDate = p.FirstAirDate;
        show.Genres = p.Genres.ToList();
        show.Keywords = p.Keywords.ToList();
        show.Popularity = p.Popularity;
        show.ExternalRating = p.Rating;
        show.SimilarExternalIds = p.SimilarExternalIds.ToList();
        show.SimilarShowIds = await ResolveSimilarAsync(p.SimilarExternalIds, ct);
        show.Status = StatusOf(p);

        if (!p.CastMissing)
        {
            detailsChanged |= await SyncCastAsync(show, p.Cast, ct);
        }
        if (p.Creatives is { } creatives)
        {
            // A show imported before creatives were counts as changed once, so consumers get them with ShowUpdated.
            detailsChanged |= await SyncCreativesAsync(show, creatives, ct) || !show.CreativesImported;
            show.CreativesImported = true;
        }
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
                : await GetOrCreatePersonAsync(member.Name, member.PhotoUrl, member.Person, ct);
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
            UpdatePerson(person, member.PhotoUrl, member.Person);

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
    private async Task<Person> GetOrCreatePersonAsync(string name, string? photoUrl, ProviderPerson? details, CancellationToken ct)
    {
        Person? person;
        if (details?.ExternalId is { } externalId)
        {
            person = db.People.Local.FirstOrDefault(p => p.ExternalId == externalId)
                ?? await db.People.FirstOrDefaultAsync(p => p.ExternalId == externalId, ct)
                ?? db.People.Local.FirstOrDefault(p => p.Name == name && p.ExternalId == null)
                ?? await db.People.FirstOrDefaultAsync(p => p.Name == name && p.ExternalId == null, ct);
        }
        else
        {
            person = db.People.Local.FirstOrDefault(p => p.Name == name)
                ?? await db.People.FirstOrDefaultAsync(p => p.Name == name, ct);
        }

        if (person is null)
        {
            person = new Person { Id = Guid.CreateVersion7(), Name = name };
            db.People.Add(person);
        }

        UpdatePerson(person, photoUrl, details);
        return person;
    }

    /// <summary>
    /// Replaces the show's creatives with the provider's. People are matched like cast (by provider id), so someone
    /// who acts in one show and produces another is one person.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    private async Task<bool> SyncCreativesAsync(Show show, IReadOnlyList<ProviderCreative> creatives, CancellationToken ct)
    {
        var changed = false;
        var wanted = new List<(Person Person, ProviderCreative Creative)>();
        foreach (var creative in creatives)
        {
            var person = await GetOrCreatePersonAsync(creative.Name, creative.PhotoUrl, creative.Person, ct);
            if (wanted.All(w => w.Person.Id != person.Id))
            {
                wanted.Add((person, creative));
            }
        }

        foreach (var existing in show.Creatives.Where(c => wanted.All(w => w.Person.Id != c.PersonId)).ToList())
        {
            show.Creatives.Remove(existing);
            changed = true;
        }

        foreach (var (order, (person, creative)) in wanted.Index())
        {
            var existing = show.Creatives.FirstOrDefault(c => c.PersonId == person.Id);
            if (existing is null)
            {
                show.Creatives.Add(new ShowCreative { ShowId = show.Id, PersonId = person.Id, Person = person, Roles = creative.Roles.ToList(), Order = order });
                changed = true;
            }
            else if (!existing.Roles.SequenceEqual(creative.Roles) || existing.Order != order)
            {
                existing.Roles = creative.Roles.ToList();
                existing.Order = order;
                changed = true;
            }
        }

        return changed;
    }

    private static void UpdatePerson(Person person, string? photoUrl, ProviderPerson? details)
    {
        if (photoUrl is not null)
        {
            person.PhotoUrl = photoUrl;
        }

        if (details is not null)
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
        foreach (var o in options.DistinctBy(o => (o.Region, o.ProviderName, o.Type)))
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
            s.ExternalRating, cast.Select(c => new CastCredit(c.Person.Name, c.Character, c.CharacterExternalIds, c.PersonId)).ToList(),
            CreativeCredits(s), s.Network, NetworkKnown: true);
    }

    /// <summary>The show's creatives for events (empty, not null, when it has none or they're still to be backfilled).</summary>
    private static List<CreativeCredit> CreativeCredits(Show s) =>
        s.Creatives.OrderBy(c => c.Order).Select(c => new CreativeCredit(c.PersonId, c.Person.Name, c.Roles)).ToList();

    /// <summary>Career figures of people with them, for consumers that rate who made a show.</summary>
    internal static List<PeopleCareersUpdatedIntegrationEvent> CareerEvents(IEnumerable<Person> people) => people
        .Where(p => p.CareerShows is not null)
        .Select(p => new PersonCareer(p.Id, p.Name, p.CareerShows!.Value, p.CareerCreated ?? 0, p.CareerExecutiveProduced ?? 0))
        .Chunk(PeopleCareersUpdatedIntegrationEvent.MaxPeople)
        .Select(chunk => new PeopleCareersUpdatedIntegrationEvent(chunk))
        .ToList();

    /// <summary>People's roles across the provider; send at most <see cref="PeopleCastCreditsUpdatedIntegrationEvent.MaxPeople"/> at a time.</summary>
    internal static PeopleCastCreditsUpdatedIntegrationEvent CastCreditsEvent(IEnumerable<(Person Person, IEnumerable<PersonCastCredit> Credits)> people) =>
        new(people
            .Select(p => new PersonCastCredits(p.Person.Id, p.Person.Name, p.Credits
                .Select(c => new ActingRole(c.ShowExternalId, c.ShowId, c.Title, c.PosterUrl, c.Status, c.Premiered, c.Popularity, c.Rating, c.Character, c.Voice))
                .ToList()))
            .ToList());

    private static ShowUpdatedIntegrationEvent ShowUpdated(Show s)
    {
        var cast = s.Cast.OrderBy(c => c.Order).ToList();
        return new(s.Id, s.Title, s.PosterUrl, s.Status, s.Genres, s.Keywords, cast.Select(c => c.Person.Name).ToList(), s.SimilarShowIds, s.Popularity,
            s.ExternalRating, cast.Select(c => new CastCredit(c.Person.Name, c.Character, c.CharacterExternalIds, c.PersonId)).ToList(),
            CreativeCredits(s), s.Network, NetworkKnown: true);
    }

    private static EpisodesScheduledIntegrationEvent EpisodesScheduled(Show s, IEnumerable<Episode> episodes) =>
        new(s.Id, s.Title, episodes
            .Select(e => new ScheduledEpisode(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirsAtUtc, e.RuntimeMinutes, e.ExternalRating))
            .ToList());
}

/// <param name="Title">The show's title; null when the provider has no such show.</param>
public readonly record struct ImportResult(ImportOutcome Outcome, string? Title);

public enum ImportOutcome
{
    Imported,
    AlreadyInCatalog,

    /// <summary>The provider has no show with that id.</summary>
    NotFound
}
